using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps;
using Questionable.Controller.Steps.Interactions;
using Questionable.Controller.Steps.Shared;
using Questionable.Controller.Utils;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using Questionable.Windows.ConfigComponents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using Quest = Questionable.Model.Quest;

namespace Questionable.Controller;

internal sealed class QuestController : MiniTaskController<QuestController>
{
    public delegate void AutomationTypeChangedEventHandler(object sender, EAutomationType e);

    public enum EAutomationType
    {
        Manual,
        Automatic,
        GatheringOnly,
        SingleQuestA,
        SingleQuestB
    }

    public enum ECurrentQuestType
    {
        Normal,
        Next,
        Gathering,
        Simulated
    }

    private const char ClipboardSeparator = ';';
    private readonly AlliedSocietyQuestFunctions _alliedSocietyQuestFunctions;
    private readonly IChatGui _chatGui;
    private readonly IClientState _clientState;

    /// <summary>只用來把聊天輸出釘回 framework 執行緒，見 <see cref="ChatGuiExtensions"/>。</summary>
    private readonly IFramework _framework;
    private readonly CombatController _combatController;
    private readonly ICondition _condition;
    private readonly Configuration _configuration;
    //private readonly IPlayerState _playerState;
    private readonly GameFunctions _gameFunctions;
    private readonly GatheringController _gatheringController;
    private readonly HighlightObject _highlightObject;
    private readonly IKeyState _keyState;
    private readonly ILogger<QuestController> _logger;
    private readonly MovementController _movementController;
    private readonly IObjectTable _objectTable;

    private readonly object _progressLock = new();

    /// <summary>
    /// 目前這條執行緒「持有 <see cref="_progressLock"/> 期間」要把副作用收到哪一份清單。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意是 <c>[ThreadStatic]</c></b>：<see cref="Stop(string, bool)"/>／
    /// <see cref="SetNextQuest"/> 這些方法同時也被 UI（繪製執行緒）與 IPC（呼叫端的執行緒）
    /// 呼叫，而那些呼叫<b>沒有</b>持鎖。做成一般欄位的話，framework 執行緒在鎖裡設好的清單
    /// 會被別條執行緒看見，於是別人的記錄被塞進我們的 <c>List</c>（裸集合，並行插入弄壞的是
    /// 集合本身），而且會在錯誤的時間點才寫出去。
    /// <para>
    /// 🔑 另外還要 <see cref="Monitor.IsEntered"/> 才算數：這個欄位在取鎖<b>之前</b>就設好，
    /// 取鎖前與放鎖後的那一小段仍然是「就地執行」，行為與改動前逐字相同。
    /// </para>
    /// </remarks>
    [ThreadStatic]
    private static List<PendingLog>? _deferralScope;
    private readonly QuestData _questData;
    private readonly QuestFunctions _questFunctions;
    private readonly QuestRegistry _questRegistry;
    private readonly SinglePlayerDutyConfigComponent _singlePlayerDutyConfigComponent;
    private readonly TaskCreator _taskCreator;
    private readonly TataruPraiseIpc _tataruPraiseIpc;
    private readonly IToastGui _toastGui;
    private EAutomationType _automationType;
    private DateTime _lastAutoRefresh = DateTime.MinValue;

    /// <summary>
    ///     Auto-refresh fields for tracking player state and progress
    /// </summary>
    private Vector3 _lastPlayerPosition = Vector3.Zero;
    private DateTime _lastProgressUpdate = DateTime.Now;
    private ElementId? _lastQuestId;
    private byte _lastQuestSequence = 255;
    private int _lastQuestStep = -1;

    /// <summary>
    /// </summary>
    private DateTime _lastTaskUpdate = DateTime.Now;

    /// <summary>
    ///     Some combat encounters finish relatively early (i.e. they're done as part of progressing the quest, but not
    ///     technically necessary to progress the quest if we'd just run away and back). We add some slight delay, as
    ///     talking to NPCs, teleporting etc. won't successfully execute.
    /// </summary>
    private DateTime _safeAnimationEnd = DateTime.MinValue;

    /// <summary>
    /// 見 <see cref="OnRepeatedInterruption"/>：卡在攻頂乙太水晶時，先隨機微幅位移，
    /// 這個時間到了才強制整個步驟重新規劃。<see langword="null"/> 代表目前沒有排定中的回復。
    /// </summary>
    private DateTime? _pendingStuckRecoveryReloadAt;

    public QuestController(
        IClientState clientState,
        IObjectTable objectTable,
        //IPlayerState playerState,
        GameFunctions gameFunctions,
        QuestFunctions questFunctions,
        MovementController movementController,
        CombatController combatController,
        GatheringController gatheringController,
        ILogger<QuestController> logger,
        HighlightObject highlightObject,
        QuestRegistry questRegistry,
        QuestData questData,
        IKeyState keyState,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        IToastGui toastGui,
        Configuration configuration,
        TaskCreator taskCreator,
        IServiceProvider serviceProvider,
        InterruptHandler interruptHandler,
        IDataManager dataManager,
        SinglePlayerDutyConfigComponent singlePlayerDutyConfigComponent,
        AlliedSocietyQuestFunctions alliedSocietyQuestFunctions,
        TataruPraiseIpc tataruPraiseIpc)
        : base(chatGui, condition, serviceProvider, interruptHandler, dataManager, logger)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        //_playerState = playerState;
        _gameFunctions = gameFunctions;
        _questFunctions = questFunctions;
        _movementController = movementController;
        _combatController = combatController;
        _gatheringController = gatheringController;
        _questRegistry = questRegistry;
        _questData = questData;
        _keyState = keyState;
        _chatGui = chatGui;
        _framework = framework;
        _condition = condition;
        _toastGui = toastGui;
        _configuration = configuration;
        _taskCreator = taskCreator;
        _singlePlayerDutyConfigComponent = singlePlayerDutyConfigComponent;
        _alliedSocietyQuestFunctions = alliedSocietyQuestFunctions;
        _tataruPraiseIpc = tataruPraiseIpc;
        _logger = logger;
        _highlightObject = highlightObject;

        _condition.ConditionChange += OnConditionChange;
        _toastGui.Toast += OnNormalToast;
        _toastGui.ErrorToast += OnErrorToast;
        _clientState.Logout += ClearRedeemAttemptsOnLogout;
    }

    public EAutomationType AutomationType
    {
        get => _automationType;
        set
        {
            if (value == _automationType)
            {
                return;
            }

            _logger.LogInformation("Setting automation type to {NewAutomationType} (previous: {OldAutomationType})",
                value, _automationType);
            _automationType = value;
            AutomationTypeChanged?.Invoke(this, value);
        }
    }

    public (QuestProgress Progress, ECurrentQuestType Type)? CurrentQuestDetails
    {
        get
        {
            if (SimulatedQuest != null)
            {
                return (SimulatedQuest, ECurrentQuestType.Simulated);
            }
            else if (NextQuest != null && _questFunctions.IsReadyToAcceptQuest(NextQuest.Quest.Id))
            {
                return (NextQuest, ECurrentQuestType.Next);
            }
            else if (GatheringQuest != null)
            {
                return (GatheringQuest, ECurrentQuestType.Gathering);
            }
            else if (StartedQuest != null)
            {
                return (StartedQuest, ECurrentQuestType.Normal);
            }
            else
            {
                return null;
            }
        }
    }

    public QuestProgress? CurrentQuest => CurrentQuestDetails?.Progress;

    public QuestProgress? StartedQuest { get; private set; }
    public QuestProgress? SimulatedQuest { get; private set; }
    public QuestProgress? NextQuest { get; private set; }
    public QuestProgress? GatheringQuest { get; private set; }

    /// <summary>
    ///     Used when accepting leves, as there's a small delay
    /// </summary>
    public QuestProgress? PendingQuest { get; private set; }

    public List<Quest> ManualPriorityQuests { get; } = [];

    public string? DebugState { get; private set; }

    public bool IsQuestWindowOpen => IsQuestWindowOpenFunction?.Invoke() ?? true;
    public Func<bool>? IsQuestWindowOpenFunction { private get; set; } = () => true;

    public bool IsRunning => !_taskQueue.AllTasksComplete;
    public TaskQueue TaskQueue => _taskQueue;

    public string? CurrentTaskState
    {
        get
        {
            if (_taskQueue.CurrentTaskExecutor is IDebugStateProvider debugStateProvider)
            {
                return debugStateProvider.GetDebugState();
            }
            else
            {
                return null;
            }
        }
    }

    public bool IsQuestingActive
    {
        get
        {
            return AutomationType == EAutomationType.Manual && !IsRunning && !IsQuestWindowOpen;
        }
    }

    public event AutomationTypeChangedEventHandler? AutomationTypeChanged;

    /// <summary>
    /// 持 <see cref="_progressLock"/> 時要做、但不能在鎖裡做的一件事：一行輸出，或一段副作用。
    /// <c>ILogger</c> 最後落到 Dalamud 的 Serilog sink，那邊自己有鎖、還會做檔案 I/O ——
    /// 在鎖裡呼叫等於把 <c>_progressLock</c> 的持有時間綁在磁碟上。跨外掛的 IPC 更糟：
    /// CallGate 是直接方法呼叫，在鎖裡打過去等於把對方的鎖排在我們的鎖後面。
    /// 所以鎖內只把要做的事記下來，出鎖之後才真的做。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>記錄那一種刻意是純資料</b>（等級／訊息模板／參數／例外），寫入放在
    /// <see cref="EmitPending"/>；<paramref name="Args"/> 在加進清單的那一刻就求值了，
    /// 所以延後寫出去的內容與「當場寫」逐字相同。
    /// <para>
    /// <paramref name="ChatMessage"/> 不是 <see langword="null"/> 時代表這一則是要印到聊天視窗的
    /// （<c>_chatGui.Print</c>）而不是寫進記錄檔；三種放在同一個清單裡，原本的先後順序才保得住。
    /// </para>
    /// </remarks>
    /// <param name="SideEffect">
    /// 不是 <see langword="null"/> 時，這一則不是輸出而是<b>一段延到出鎖之後才執行的副作用</b>
    /// —— 跨外掛 IPC（<c>TataruPraiseIpc.NotifyNeedHelp</c>），或需要重建 <c>BeginScope</c>
    /// 前綴的記錄。一律經由 <see cref="RunOrDefer"/> 加進來。
    /// </param>
    private readonly record struct PendingLog(
        LogLevel Level,
        string Template,
        object?[] Args,
        string? ChatMessage,
        Exception? Exception = null,
        System.Action? SideEffect = null);

    /// <summary>把鎖內記下來的訊息寫出去。<b>必須在 <see cref="_progressLock"/> 放掉之後才呼叫。</b></summary>
    private void EmitPending(List<PendingLog> pending)
    {
        foreach(PendingLog entry in pending)
        {
            if (entry.SideEffect is { } sideEffect)
            {
                // 🔴 一則失敗不可以讓後面的都不做：延後的清單裡可能同時有「停止移動」與
                //    「請塔塔露喊一句」，而它們彼此無關。
                try
                {
                    sideEffect();
                }
                catch(Exception e)
                {
                    _logger.LogError(e, "Deferred side effect failed");
                }
            }
            else if (entry.ChatMessage is { } chatMessage)
            {
                _chatGui.Print(chatMessage, CommandHandler.MessageTag, CommandHandler.TagColor);
            }
            else
            {
                _logger.Log(entry.Level, entry.Exception, entry.Template, entry.Args);
            }
        }

        pending.Clear();
    }

    /// <summary>
    /// 現在是不是「持有 <see cref="_progressLock"/>、而且有人在收延後清單」。
    /// </summary>
    private bool TryGetDeferralScope([NotNullWhen(true)] out List<PendingLog>? pending)
    {
        pending = Monitor.IsEntered(_progressLock) ? _deferralScope : null;
        return pending != null;
    }

    /// <summary>
    /// 寫一行記錄；持著 <see cref="_progressLock"/> 時改成收進延後清單。
    /// </summary>
    /// <remarks>
    /// 📌 <paramref name="args"/> 在<b>呼叫的那一刻</b>就求值了，所以延後寫出去的內容
    /// 與改動前逐字相同（不會變成「出鎖之後才去讀 <c>CurrentQuest</c>」）。
    /// </remarks>
    private void LogOrDefer(LogLevel level, string template, params object?[] args)
    {
        LogOrDefer(level, null, template, args);
    }

    /// <inheritdoc cref="LogOrDefer(LogLevel, string, object?[])"/>
    private void LogOrDefer(LogLevel level, Exception? exception, string template, params object?[] args)
    {
        if (TryGetDeferralScope(out List<PendingLog>? pending))
        {
            pending.Add(new PendingLog(level, template, args, null, exception));
        }
        else
        {
            _logger.Log(level, exception, template, args);
        }
    }

    /// <summary>
    /// 做一件事；持著 <see cref="_progressLock"/> 時改成收進延後清單，出鎖之後才做。
    /// </summary>
    /// <remarks>
    /// 🔴 用在<b>跨外掛 IPC</b>（CallGate 是直接方法呼叫，會在我們的鎖裡面跑別的外掛的碼
    /// ⇒ 對方日後長出任何一條回頭呼叫的路徑就是死鎖）與「要保留 <c>BeginScope</c> 前綴」的記錄。
    /// <para>
    /// ⚠️ 只適合<b>純副作用</b>：回傳值會在鎖裡被拿去分支的呼叫不可以用這一支。
    /// </para>
    /// <para>
    /// 📌 沒有人在收延後清單時（不持鎖、或從 UI／IPC 進來）就<b>當場執行</b>，
    /// 行為與改動前逐字相同。
    /// </para>
    /// </remarks>
    private void RunOrDefer(System.Action action)
    {
        if (TryGetDeferralScope(out List<PendingLog>? pending))
        {
            pending.Add(new PendingLog(LogLevel.None, string.Empty, [], null, null, action));
        }
        else
        {
            action();
        }
    }

    public void Reload()
    {
        // 見 <see cref="PendingLog"/>：這一行原本是鎖內的第一個敘述，訊息是常數、也不看任何
        // 鎖保護的狀態 ⇒ 直接搬到取鎖之前。等級、文字、觸發條件與先後順序全部不變。
        _logger.LogInformation("Reload, resetting curent quest progress");

        // 🔴 檔案列舉、JSON 解析、驗證全部搬到 _progressLock 外面 —— 那把鎖每一個 framework tick
        //    都會被 UpdateCurrentQuest 拿去，而原本這一整段是在它裡面跑的。序列化改由 QuestRegistry
        //    自己那把「只在重新載入這條路徑上會被取」的閘門負責（卡住重試計時器在 framework 執行緒、
        //    使用者按下重新載入在繪製執行緒，兩邊可以同時進來）。
        QuestRegistry.ReloadBatch registryReload = _questRegistry.PrepareReload();
        bool published = false;
        try
        {
            lock(_progressLock)
            {
                ResetInternalState();
                ResetAutoRefreshState();

                // 鎖裡只剩「把準備好的登錄換上去」這幾行常數時間的參照指派。
                published = _questRegistry.PublishReload(registryReload);
                _singlePlayerDutyConfigComponent.Reload();
                _alliedSocietyQuestFunctions.Reload();
            }
        }
        finally
        {
            // 🔴 記錄、Reloaded 事件、跨外掛的 IPC 廣播一律在鎖外：Reloaded 的訂閱端會再做一次完整的
            //    檔案系統列舉，而 SendMessage 是同步跑在我們這條執行緒上的、別的外掛的碼。
            // ⚠️ 有一處先後順序變了：Reloaded 事件與 IPC 廣播原本排在
            //    SinglePlayerDutyConfigComponent.Reload() 之前，現在排在它之後。兩邊互不相依
            //    （前者讀任務登錄、後者也讀任務登錄，都在替換之後），訂閱端看到的狀態只會更完整。
            _questRegistry.EmitReloadSideEffects(registryReload, published);
        }
    }

    private void ResetInternalState()
    {
        StartedQuest = null;
        NextQuest = null;
        GatheringQuest = null;
        PendingQuest = null;
        SimulatedQuest = null;
        _safeAnimationEnd = DateTime.MinValue;
        _pendingStuckRecoveryReloadAt = null;

        DebugState = null;
    }

    private void ResetAutoRefreshState()
    {
        _lastPlayerPosition = Vector3.Zero;
        _lastQuestStep = -1;
        _lastQuestSequence = 255;
        _lastQuestId = null;
        _lastProgressUpdate = DateTime.Now;
        _lastAutoRefresh = DateTime.Now;
        ConsecutiveInterruptions = 0;
    }

    public void Update()
    {
        if (_pendingStuckRecoveryReloadAt is { } dueAt && DateTime.Now >= dueAt)
        {
            _pendingStuckRecoveryReloadAt = null;
            _logger.LogInformation("Nudge-position stuck recovery: reloading current step now");
            Reload();
        }

        unsafe
        {
            ActionManager* actionManager = ActionManager.Instance();
            if (actionManager != null)
            {
                float animationLock = Math.Max(actionManager->AnimationLock,
                    actionManager->CastTimeElapsed > 0
                        ? actionManager->CastTimeTotal - actionManager->CastTimeElapsed
                        : 0);
                if (animationLock > 0)
                {
                    _safeAnimationEnd = DateTime.Now.AddSeconds(1 + animationLock);
                }
            }
        }

        if (IsQuestingActive)
        {
            return;
        }

        UpdateCurrentQuest();

        if (!_clientState.IsLoggedIn)
        {
            StopAllDueToConditionFailed("Logged out");
        }
        if (_condition[ConditionFlag.Unconscious])
        {
            if (_condition[ConditionFlag.Unconscious] &&
                _condition[ConditionFlag.SufferingStatusAffliction63] &&
                _clientState.TerritoryType == SinglePlayerDuty.SpecialTerritories.Lahabrea)
            {
                // ignore, we're in the lahabrea fight
            }
            else if (_taskQueue.CurrentTaskExecutor is Duty.WaitAutoDutyExecutor)
            {
                // ignoring death in a dungeon if it is being run by AD
            }
            else if (!_taskQueue.AllTasksComplete)
            {
                StopAllDueToConditionFailed("HP = 0", true);
            }
        }
        else if (_configuration.General.UseEscToCancelQuesting && _keyState[VirtualKey.ESCAPE])
        {
            if (!_taskQueue.AllTasksComplete)
            {
                StopAllDueToConditionFailed("ESC pressed");
            }
        }

        // check level stop condition
        // stops immediately instead of quest stop after completion of quest
        if (_configuration.Stop.Enabled && _configuration.Stop.LevelToStopAfter)
        {
            unsafe
            {
                short currentLevel = PlayerState.Instance()->CurrentLevel;
                if (currentLevel >= _configuration.Stop.TargetLevel && IsRunning)
                {
                    _logger.LogInformation("Reached level stop condition (level: {CurrentLevel}, target: {TargetLevel})", currentLevel, _configuration.Stop.TargetLevel);
                    _chatGui.Print($"Reached or exceeded target level {_configuration.Stop.TargetLevel}.", CommandHandler.MessageTag, CommandHandler.TagColor);
                    Stop($"Level stop condition reached [{currentLevel}]");
                    return;
                }
            }
        }

        if (AutomationType == EAutomationType.Automatic &&
            (_taskQueue.AllTasksComplete || _taskQueue.CurrentTaskExecutor?.CurrentTask is WaitAtEnd.WaitQuestAccepted)
            && CurrentQuest is { Sequence: 0, Step: 0 } or { Sequence: 0, Step: 255 }
            && DateTime.Now >= CurrentQuest.StepProgress.StartedAt.AddSeconds(15))
        {
            // 見 <see cref="PendingLog"/>：原本是鎖內的第一個敘述，訊息是常數、也不看任何
            // 鎖保護的狀態 ⇒ 搬到取鎖之前，先後順序完全不變。
            _logger.LogWarning("Quest accept apparently didn't work out, resetting progress");

            lock(_progressLock)
            {
                CurrentQuest.SetStep(0);
            }

            ExecuteNextStep();
            return;
        }

        CheckAutoRefreshCondition();

        UpdateCurrentTask();
    }

    private void CheckAutoRefreshCondition()
    {
        if (!_configuration.General.AutoStepRefreshEnabled ||
            AutomationType != EAutomationType.Automatic ||
            !IsRunning ||
            CurrentQuest == null ||
            !_clientState.IsLoggedIn ||
            _objectTable[0] == null ||
            DateTime.Now < _lastAutoRefresh.AddSeconds(5))
        {
            return;
        }

        if (_condition[ConditionFlag.InCombat] ||
            _condition[ConditionFlag.Unconscious] ||
            _condition[ConditionFlag.BoundByDuty] ||
            _condition[ConditionFlag.InDeepDungeon] ||
            _condition[ConditionFlag.WatchingCutscene] ||
            _condition[ConditionFlag.WatchingCutscene78] ||
            _condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51] ||
            _gameFunctions.IsOccupied() ||
            !_movementController.IsNavmeshReady ||
            (_taskQueue.CurrentTaskExecutor?.CurrentTask.GetType().Namespace == typeof(WaitAtEnd).Namespace
                && ConsecutiveInterruptions < 3) ||
            DateTime.Now < _safeAnimationEnd)
        {
            _lastProgressUpdate = DateTime.Now;
            return;
        }

        Vector3 currentPosition = _objectTable[0]!.Position;
        ElementId currentQuestId = CurrentQuest.Quest.Id;
        byte currentSequence = CurrentQuest.Sequence;
        int currentStep = CurrentQuest.Step;

        bool hasProgressBeenMade =
            Vector3.Distance(currentPosition, _lastPlayerPosition) > 0.5f ||
            !currentQuestId.Equals(_lastQuestId) ||
            currentSequence != _lastQuestSequence ||
            currentStep != _lastQuestStep;

        if (hasProgressBeenMade)
        {
            _lastPlayerPosition = currentPosition;
            _lastQuestId = currentQuestId;
            _lastQuestSequence = currentSequence;
            _lastQuestStep = currentStep;
            _lastProgressUpdate = DateTime.Now;
            ConsecutiveInterruptions = 0;
        }
        else
        {
            // we detect no progress, check if we should auto-refresh
            TimeSpan timeSinceProgress = DateTime.Now - _lastProgressUpdate;
            TimeSpan refreshDelay = TimeSpan.FromSeconds(_configuration.General.AutoStepRefreshDelaySeconds);

            if (timeSinceProgress >= refreshDelay)
            {
                _logger.LogInformation("Automatically refreshing quest step as no progress detected for {TimeSinceProgress:F1} seconds (quest: {QuestId}, sequence: {Sequence}, step: {Step})",
                    timeSinceProgress.TotalSeconds, currentQuestId, currentSequence, currentStep);

                _chatGui.Print($"Automatically refreshing quest step as no progress detected for {timeSinceProgress.TotalSeconds:F0} seconds.",
                    CommandHandler.MessageTag, CommandHandler.TagColor);

                ClearTasksInternal();

                Reload();

                _lastAutoRefresh = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// 連續中斷重試達到門檻（見 MiniTaskController.ConsecutiveInterruptions）時的主動回復。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意只處理一種情境：目的地是乙太水晶，人已經到了，卻沒有成功攻頂/傳送。</b>
    /// 其他互動類型（NPC 對話、拾取、戰鬥動作……）持續失敗的重試迴圈先不處理，
    /// 交給被動的 AutoStepRefresh（<see cref="CheckAutoRefreshCondition"/>）去兜底——
    /// 那些情境的成因太雜，貿然套用同一套回復手段風險比不處理還高。
    /// <para>
    /// 📌 <b>回復手段是「隨機微幅位移＋重新規劃路線」，不是傳送走。</b>
    /// 這種卡住通常是 vnavmesh／遊戲判定角色「已經到了」，但實際位置或朝向差了一點，
    /// 導致攻頂互動判定不到——手動走一小段路就會恢復正常，正是這裡在做的事：
    /// 隨機挑一個約 1.5~3 碼外的鄰近點導航過去，讓角色物理上真的移動一段距離，
    /// 兩秒後再強制整個步驟重新規劃（<see cref="ClearTasksInternal"/> + <see cref="Reload"/>）。
    /// 不會把角色傳送到別的地方，微幅移動用肉眼幾乎看不出來。
    /// </para>
    /// <para>
    /// 🔴 只在非戰鬥時動作：戰鬥中要嘛不該在做這個任務步驟，要嘛移動本身另有戰鬥系統處理。
    /// </para>
    /// <para>
    /// ⚠️ <b>「位置／朝向差一點」是經驗推測，不是證明。</b>
    /// 這個成因是原作者從實際卡住的情況歸納出來的，我們沒有離線或實機證據能證實它，
    /// 也沒有辦法在不實機的情況下證實。
    /// <br/>
    /// 📌 <b>假設不成立時的後果：白走 1.5~3 碼，然後照樣重新規劃路線。</b>
    /// 角色只是多繞一小段路，不會卡死、不會傳送、不會離開原地附近，
    /// 真正的兜底仍然是被動的 <see cref="CheckAutoRefreshCondition"/>（AutoStepRefresh）。
    /// 也就是說，這個推測猜錯的代價是「沒幫上忙」，不是「把事情弄壞」。
    /// </para>
    /// </remarks>
    protected override void OnRepeatedInterruption(int consecutiveCount)
    {
        if (consecutiveCount != 3 ||
            !_configuration.General.NudgePositionOnAetheryteAttuneStuck ||
            _condition[ConditionFlag.InCombat] ||
            _objectTable[0] == null ||
            _taskQueue.CurrentTaskExecutor?.CurrentTask is not Aetheryte.Attune attune)
        {
            return;
        }

        Vector3 currentPosition = _objectTable[0]!.Position;
        double angle = Random.Shared.NextDouble() * Math.PI * 2;
        float nudgeDistance = 1.5f + (float)Random.Shared.NextDouble() * 1.5f;
        Vector3 nudgeTarget = currentPosition with
        {
            X = currentPosition.X + (float)Math.Cos(angle) * nudgeDistance,
            Z = currentPosition.Z + (float)Math.Sin(angle) * nudgeDistance
        };

        _logger.LogWarning(
            "Repeated interruption ({Count}) while attuning aetheryte {Aetheryte}, nudging position by ~{Distance:F1}y and recalculating route",
            consecutiveCount, attune.AetheryteLocation, nudgeDistance);

        _chatGui.Print(
            $"Repeated interruptions attuning {attune.AetheryteLocation}, nudging position and recalculating route.",
            CommandHandler.MessageTag, CommandHandler.TagColor);

        ClearTasksInternal();
        _movementController.NavigateTo(EMovementType.None, null, nudgeTarget, false, false, 0.5f);
        _pendingStuckRecoveryReloadAt = DateTime.Now.AddSeconds(2);
    }

    private void UpdateCurrentQuest()
    {
        List<PendingLog> pending = [];
        List<PendingLog>? previousScope = _deferralScope;
        _deferralScope = pending;
        try
        {
            UpdateCurrentQuestLocked(pending);
        }
        finally
        {
            _deferralScope = previousScope;
            // 出鎖之後才寫（<c>lock</c> 自己的 <c>Monitor.Exit</c> 在內層的 finally，一定先跑）。
            // ⚠️ 這個鎖裡還有別的元件（TaskCreator／MovementController／CombatController…）
            // 自己在寫記錄，那些沒有被延後 ⇒ 這裡延後的幾行相對於它們會往後挪；
            // 延後的這幾行彼此之間的先後順序不變。
            EmitPending(pending);
        }
    }

    /// <summary><see cref="UpdateCurrentQuest"/> 持鎖的那一段。</summary>
    /// <remarks>
    /// 🔴 這裡面持有 <see cref="_progressLock"/>：<b>不要直接呼叫 <c>_logger</c>、<c>_chatGui</c>，
    /// 也不要直接打任何跨外掛的 IPC</b>。要寫的東西用 <c>pending.Add</c> 或 <see cref="LogOrDefer"/>，
    /// 要做的副作用用 <see cref="RunOrDefer"/>，兩者都會等出鎖之後才真的發生。
    /// </remarks>
    private void UpdateCurrentQuestLocked(List<PendingLog> pending)
    {
        lock(_progressLock)
        {
            DebugState = null;

            if (!_clientState.IsLoggedIn)
            {
                ResetInternalState();
                DebugState = "Not logged in";
                return;
            }

            if (PendingQuest != null)
            {
                if (!_questFunctions.IsQuestAccepted(PendingQuest.Quest.Id))
                {
                    DebugState = $"Waiting for Leve {PendingQuest.Quest.Id}";
                    return;
                }
                else
                {
                    StartedQuest = PendingQuest;
                    PendingQuest = null;
                    CheckNextTasks("Pending quest accepted");
                }
            }

            if (SimulatedQuest == null && NextQuest != null)
            {
                // if the quest is accepted, we no longer track it
                bool canUseNextQuest;
                if (NextQuest.Quest.Info.IsRepeatable)
                {
                    canUseNextQuest = !_questFunctions.IsQuestAccepted(NextQuest.Quest.Id);
                }
                else
                {
                    canUseNextQuest = !_questFunctions.IsQuestAcceptedOrComplete(NextQuest.Quest.Id);
                }

                if (!canUseNextQuest)
                {
                    pending.Add(new PendingLog(LogLevel.Information,
                        "Next quest {QuestId} accepted or completed",
                        [NextQuest.Quest.Id], null));

                    if (AutomationType == EAutomationType.SingleQuestA)
                    {
                        StartedQuest = NextQuest;
                        AutomationType = EAutomationType.SingleQuestB;
                    }

                    pending.Add(new PendingLog(LogLevel.Debug, "Started: {StartedQuest}",
                        [StartedQuest?.Quest.Id], null));
                    NextQuest = null;
                }
            }

            QuestProgress? questToRun;
            byte currentSequence;
            if (SimulatedQuest != null)
            {
                currentSequence = SimulatedQuest.Sequence;
                questToRun = SimulatedQuest;
            }
            else if (NextQuest != null && _questFunctions.IsReadyToAcceptQuest(NextQuest.Quest.Id))
            {
                questToRun = NextQuest;
                currentSequence = NextQuest.Sequence; // by definition, this should always be 0
                if (NextQuest.Step == 0 &&
                    _taskQueue.AllTasksComplete &&
                    AutomationType == EAutomationType.Automatic)
                {
                    ExecuteNextStep();
                }
            }
            else if (GatheringQuest != null)
            {
                questToRun = GatheringQuest;
                currentSequence = GatheringQuest.Sequence;
                if (GatheringQuest.Step == 0 &&
                    _taskQueue.AllTasksComplete &&
                    AutomationType == EAutomationType.Automatic)
                {
                    ExecuteNextStep();
                }
            }
            else
            {
                // 🔴 用不印東西的那一支：GetCurrentQuest 會直接 _chatGui.Print，而這裡持著 _progressLock。
                //    提示收進延後清單、出鎖之後才印；去重（同一則 60 秒一次）在 PrintHintThrottled 裡面。
                (ElementId? currentQuestId, currentSequence, MainScenarioQuestState msqState) =
                    _questFunctions.GetCurrentQuestWithHint(allowNewMsq: AutomationType != EAutomationType.SingleQuestB,
                        out string? questHint);
                if (questHint != null)
                {
                    RunOrDefer(() => _questFunctions.PrintHintThrottled(questHint));
                }

                (ElementId, byte)? priorityQuestOption =
                    ManualPriorityQuests
                        .Where(x => _questFunctions.IsReadyToAcceptQuest(x.Id) || _questFunctions.IsQuestAccepted(x.Id))
                        .Select(x => (x.Id, _questFunctions.GetQuestProgressInfo(x.Id)?.Sequence ?? 0))
                        .FirstOrDefault();
                if (priorityQuestOption is { Item1: not null } priorityQuest)
                {
                    currentQuestId = priorityQuest.Item1;
                    currentSequence = priorityQuest.Item2;
                }

                if (currentQuestId == null || currentQuestId.Value == 0)
                {
                    if (StartedQuest != null)
                    {
                        if (msqState == MainScenarioQuestState.Unavailable)
                        {
                            //_logger.LogWarning("MSQ information not available, doing nothing");
                            return;
                        }
                        else if (msqState == MainScenarioQuestState.LoadingScreen)
                        {
                            //_logger.LogWarning("On loading screen, no MSQ - doing nothing");
                            return;
                        }

                        pending.Add(new PendingLog(LogLevel.Information,
                            "No current quest, resetting data [CQI: {CurrrentQuestData}], [CQ: {QuestData}], [MSQ: {MsqData}]",
                            [
                                _questFunctions.GetCurrentQuestInternal(true),
                                // ⚠️ 這裡刻意丟掉提示（out _）：上面那次查詢已經在同一幀收下它了，
                                //    這一行只是診斷用的重查，再印一次是重複。
                                _questFunctions.GetCurrentQuestWithHint(true, out _),
                                _questFunctions.GetMainScenarioQuest()
                            ], null));
                        StartedQuest = null;
                        Stop("Resetting current quest");
                    }

                    questToRun = null;
                }
                else if (StartedQuest == null || StartedQuest.Quest.Id != currentQuestId)
                {
                    if (_configuration.Stop.Enabled &&
                        StartedQuest != null &&
                        _configuration.Stop.QuestsToStopAfter.Contains(StartedQuest.Quest.Id) &&
                        _questFunctions.IsQuestComplete(StartedQuest.Quest.Id))
                    {
                        ElementId questId = StartedQuest.Quest.Id;
                        pending.Add(new PendingLog(LogLevel.Information,
                            "Reached stopping point (quest: {QuestId})", [questId], null));
                        pending.Add(new PendingLog(LogLevel.None, string.Empty, [],
                            $"Completed quest '{StartedQuest.Quest.Info.Name}', which is configured as a stopping point."));
                        StartedQuest = null;
                        Stop($"Stopping point [{questId}] reached");
                    }
                    else if (_questRegistry.TryGetQuest(currentQuestId, out Quest? quest))
                    {
                        _highlightObject.SetHighlight([]);
                        pending.Add(new PendingLog(LogLevel.Information, "New quest: {QuestName}",
                            [quest.Info.Name], null));
                        StartedQuest = new(quest, currentSequence);
#if DEBUG
                        if (_configuration.Advanced.OpenEditor)
                        {
                            (bool success, string msg) = _questRegistry.OpenEditor(StartedQuest.Quest.Info);
                            pending.Add(new PendingLog(LogLevel.Debug,
                                $"OpenEditor {success}: {msg}", [], null));
                        }
#endif

                        unsafe
                        {
                            if (PlayerState.Instance()->CurrentLevel < quest.Info.Level)
                            {
                                pending.Add(new PendingLog(LogLevel.Information,
                                    "Stopping automation, player level ({PlayerLevel}) < quest level ({QuestLevel}",
                                    [PlayerState.Instance()->CurrentLevel, quest.Info.Level], null));
                                Stop("Quest level too high", true);
                            }
                            else
                            {
                                if (AutomationType == EAutomationType.SingleQuestB)
                                {
                                    pending.Add(new PendingLog(LogLevel.Information,
                                        "Single quest is finished", [], null));
                                    AutomationType = EAutomationType.Manual;
                                }

                                CheckNextTasks("Different Quest");
                            }
                        }
                    }
                    else if (StartedQuest != null)
                    {
                        pending.Add(new PendingLog(LogLevel.Information,
                            "No active quest anymore? Not sure what happened...", [], null));
                        StartedQuest = null;
                        Stop("No active Quest", true);
                    }

                    return;
                }
                else
                {
                    questToRun = StartedQuest;
                }
            }

            if (questToRun == null)
            {
                DebugState = "No quest active";
                Stop("No quest active");
                return;
            }

            if (_gameFunctions.IsOccupied() && !_gameFunctions.IsOccupiedWithCustomDeliveryNpc(questToRun.Quest))
            {
                DebugState = "Occupied";
                return;
            }

            // 🔴 讀的是快照不是即時值：IsPathRunning 的 getter 是一次跨外掛 IPC，而這裡持著
            //    _progressLock。快照由 DalamudInitializer 在同一幀、進到這裡之前取樣，
            //    而 MovementController.Stop()／ResetPathfinding() 會當場把它歸零
            //    ⇒ 「鎖內停止移動之後再判斷」的結果與改動前相同。見 IsPathRunningSnapshot。
            if (_movementController.IsPathfindingSnapshot)
            {
                DebugState = "Pathfinding is running";
                return;
            }

            if (_movementController.IsPathRunningSnapshot)
            {
                DebugState = "Path is running";
                return;
            }

            if (DateTime.Now < _safeAnimationEnd)
            {
                DebugState = "Waiting for Animation";
                return;
            }

            if (questToRun.Sequence != currentSequence)
            {
                _highlightObject.SetHighlight([]);
                questToRun.SetSequence(currentSequence);
                CheckNextTasks(
                    $"New sequence {questToRun == StartedQuest}/{_questFunctions.GetCurrentQuestInternal(true)}");
            }

            Quest q = questToRun.Quest;
            QuestSequence? sequence = q.FindSequence(questToRun.Sequence);
            if (sequence == null)
            {
                DebugState = $"Sequence {questToRun.Sequence} not found";
                Stop("Unknown sequence", true);
                return;
            }

            if (questToRun.Step == 255)
            {
                DebugState = "Step completed";
                if (!_taskQueue.AllTasksComplete)
                {
                    CheckNextTasks("Step complete");
                }
                return;
            }

            if (sequence.Steps.Count > 0 && questToRun.Step >= sequence.Steps.Count)
            {
                DebugState = "Step not found";
                Stop("Unknown step", true);
                return;
            }

            DebugState = null;
        }
    }

    public (QuestSequence? Sequence, QuestStep? Step, bool createTasks) GetNextStep()
    {
        if (CurrentQuest == null)
        {
            return (null, null, false);
        }

        Quest q = CurrentQuest.Quest;
        QuestSequence? seq = q.FindSequence(CurrentQuest.Sequence);
        if (seq == null)
        {
            return (null, null, true);
        }

        if (seq.Steps.Count == 0)
        {
            return (seq, null, true);
        }

        if (CurrentQuest.Step >= seq.Steps.Count)
        {
            return (null, null, false);
        }

        return (seq, seq.Steps[CurrentQuest.Step], true);
    }

    public void IncreaseStepCount(ElementId? questId, int? sequence, bool shouldContinue = false)
    {
        List<PendingLog> pending = [];
        List<PendingLog>? previousScope = _deferralScope;
        _deferralScope = pending;
        bool stepped;
        try
        {
            stepped = IncreaseStepCountLocked(questId, sequence, pending);
        }
        finally
        {
            _deferralScope = previousScope;
            // 出鎖之後才寫，提早結束的路徑也會經過這裡。這個鎖裡沒有別的元件在寫記錄，
            // 所以延後之後的先後順序與原本完全一樣；下面 ExecuteNextStep() 產生的記錄
            // 仍然排在這幾行之後。
            EmitPending(pending);
        }

        if (!stepped)
        {
            return;
        }

        using IDisposable? scope = _logger.BeginScope("IncStepCt");
        if (shouldContinue && AutomationType != EAutomationType.Manual)
        {
            ExecuteNextStep();
        }
    }

    /// <summary><see cref="IncreaseStepCount"/> 持鎖的那一段。</summary>
    /// <param name="pending">要寫的記錄一律加進這裡，由呼叫端出鎖之後才寫出去。</param>
    /// <returns>
    /// <see langword="false"/>＝原本那兩個提早 <c>return</c> 的路徑（沒有真的增加步數）。
    /// </returns>
    /// <remarks>
    /// 🔴 這裡面持有 <see cref="_progressLock"/>：不要直接呼叫 <c>_logger</c> 或 <c>_chatGui</c>。
    /// <para>
    /// <see cref="SkipLocked"/> 直接呼叫這一支（而不是 <see cref="IncreaseStepCount"/>），
    /// 因為它自己已經持有同一把鎖——走公開那支的話，延後的記錄會在鎖還握著的時候被寫出去。
    /// 它原本傳的 <c>shouldContinue</c> 就是預設的 <see langword="false"/>，
    /// 所以少掉的那段（<c>BeginScope</c> ＋ 不會成立的 <c>if</c>）沒有任何可觀察的作用。
    /// </para>
    /// </remarks>
    private bool IncreaseStepCountLocked(ElementId? questId, int? sequence, List<PendingLog> pending)
    {
        lock(_progressLock)
        {
            (QuestSequence? seq, QuestStep? step, bool _) = GetNextStep();
            if (CurrentQuest == null || seq == null || step == null)
            {
                pending.Add(new PendingLog(LogLevel.Warning,
                    "Unable to retrieve next quest step, not increasing step count", [], null));
                return false;
            }

            if (questId != null && CurrentQuest.Quest.Id != questId)
            {
                pending.Add(new PendingLog(LogLevel.Warning,
                    "Ignoring 'increase step count' for different quest (expected {ExpectedQuestId}, but we are at {CurrentQuestId}",
                    [questId, CurrentQuest.Quest.Id], null));
                return false;
            }

            if (sequence != null && seq.Sequence != sequence.Value)
            {
                pending.Add(new PendingLog(LogLevel.Warning,
                    "Ignoring 'increase step count' for different sequence (expected {ExpectedSequence}, but we are at {CurrentSequence}",
                    [sequence, seq.Sequence], null));
            }

            pending.Add(new PendingLog(LogLevel.Information,
                "Increasing step count from {CurrentValue}", [CurrentQuest.Step], null));
            if (CurrentQuest.Step + 1 < seq.Steps.Count)
            {
                CurrentQuest.SetStep(CurrentQuest.Step + 1);
            }
            else
            {
                CurrentQuest.SetStep(255);
            }

            ResetAutoRefreshState();
        }

        return true;
    }

    internal void AbandonQuest(QuestId questId)
    {
        _logger.LogInformation($"AbandonQuest: {questId}");
        _logger.LogWarning("Abandoning a quest via direct command is not available on this Dalamud API level; use the in-game journal to abandon it manually.");
    }

    internal void AbandonQuest(string questId)
    {
        try
        {
            ushort parsedQuestId = ushort.Parse(questId, CultureInfo.InvariantCulture);
            if (_questFunctions.GetQuestProgressInfo(new QuestId(parsedQuestId)) != null)
            {
                AbandonQuest(new QuestId(parsedQuestId));
            }
            else
            {
                _logger.LogWarning("AbandonQuest failed: could not find quest ID");
            }
        }
        catch(Exception e)
        {
            _logger.LogWarning($"AbandonQuest failed: {e}");
        }
    }

    internal void AbandonQuest()
    {
        if (CurrentQuest != null && _questFunctions.GetQuestProgressInfo(CurrentQuest.Quest.Id) != null)
        {
            AbandonQuest((QuestId)CurrentQuest.Quest.Id);
        }
        else
        {
            _logger.LogWarning("AbandonQuest failed: could not find quest ID");
        }
    }

    private void ClearTasksInternal()
    {
        //_logger.LogDebug("Clearing task (internally)");
        if (_taskQueue.CurrentTaskExecutor is IStoppableTaskExecutor stoppableTaskExecutor)
        {
            stoppableTaskExecutor.StopNow();
        }

        _taskQueue.Reset();

        _combatController.Stop("ClearTasksInternal", RunOrDefer);
        _gatheringController.Stop("ClearTasksInternal");
    }

    public override void Stop(string label)
    {
        Stop(label, false);
    }

    /// <summary>
    /// 停止自動任務。
    /// </summary>
    /// <param name="label">停止原因，寫進記錄檔。</param>
    /// <param name="needsManualAttention">
    /// 這次停止是不是「卡住了，要人來看一下」——不支援的步驟、任務跑不下去、資料對不上之類。
    /// 使用者自己按停止、走到設定好的停止點、流程正常結束都<b>不算</b>。
    /// </param>
    /// <remarks>
    /// 🔴 <b>通知刻意寫在下面那個 <c>if</c> 裡面。</b>那個判斷本身就是「從執行中變成停下來」的狀態邊緣：
    /// 進得去代表這一幀真的把自動化停掉了，第二幀（<c>IsRunning</c> 已經是 false、
    /// <c>AutomationType</c> 已經被設成 <see cref="EAutomationType.Manual"/>）就進不去。
    /// <c>Stop("Unknown sequence")</c> 這類呼叫點是每幀都會走到的輪詢路徑，
    /// 少了這道邊緣就會變成「一直念」——而那是不會報錯的。
    /// </remarks>
    public void Stop(string label, bool needsManualAttention)
    {
        _highlightObject.SetHighlight([]);
        using IDisposable? scope = _logger.BeginScope($"Stop/{label}");
        if (IsRunning || AutomationType != EAutomationType.Manual)
        {
            ClearTasksInternal();

            // 🔑 延後的時候把 BeginScope 一起帶過去重建，輸出的前綴與改動前逐字相同。
            RunOrDefer(() =>
            {
                using IDisposable? deferredScope = _logger.BeginScope($"Stop/{label}");
                _logger.LogInformation("Stopping automatic questing");
            });
            AutomationType = EAutomationType.Manual;
            NextQuest = null;
            GatheringQuest = null;
            _lastTaskUpdate = DateTime.Now;

            ResetAutoRefreshState();
            unsafe
            {
                if (_objectTable[0] is IPlayerCharacter player)
                {
                    StatusManager* playerStatusManager = player.BattleChara()->GetStatusManager();
                    if (playerStatusManager->HasStatus(416)) // Transparent
                    {
                        StatusManager.ExecuteStatusOff(416);
                    }
                }
            }

            if (needsManualAttention)
            {
                // 🔴 這是跨外掛 IPC（CallGate＝直接方法呼叫，對方的碼跑在我們這條執行緒上）。
                //    UpdateCurrentQuest／Skip 是持著 _progressLock 進來的，在鎖裡打過去等於
                //    把 TataruPraise 的鎖排在我們的鎖後面 —— 對方日後長出任何一條回頭呼叫
                //    Questionable 的路徑就是死鎖。純通知、沒有回傳值 ⇒ 延到出鎖之後再打。
                RunOrDefer(() => _tataruPraiseIpc.NotifyNeedHelp(label));
            }
        }
    }

    /// <inheritdoc/>
    protected override void StopDueToFailure(string label)
    {
        Stop(label, true);
    }

    public void StopAllDueToConditionFailed(string label)
    {
        StopAllDueToConditionFailed(label, false);
    }

    /// <param name="needsManualAttention">見 <see cref="Stop(string, bool)"/>。</param>
    /// <inheritdoc cref="StopAllDueToConditionFailed(string)"/>
    public void StopAllDueToConditionFailed(string label, bool needsManualAttention)
    {
        Stop(label, needsManualAttention);
        _movementController.Stop(RunOrDefer);
        _combatController.Stop(label, RunOrDefer);
        _gatheringController.Stop(label);
    }

    private void CheckNextTasks(string label)
    {
        if (AutomationType is EAutomationType.Automatic or EAutomationType.SingleQuestA or EAutomationType.SingleQuestB)
        {
            using IDisposable? scope = _logger.BeginScope(label);

            ClearTasksInternal();

            if (CurrentQuest?.Step is >= 0 and < 255)
            {
                ExecuteNextStep();
            }
            else
            {
                RunOrDefer(() =>
                {
                    using IDisposable? deferredScope = _logger.BeginScope(label);
                    _logger.LogInformation("Couldn't execute next step during Stop() call");
                });
            }

            _lastTaskUpdate = DateTime.Now;

            ResetAutoRefreshState();
        }
        else
        {
            Stop(label);
        }
    }

    public void SimulateQuest(IQuestInfo? questInfo, byte sequence, int step)
    {
        if (questInfo is not null && _questRegistry.TryGetQuest(questInfo.QuestId, out Quest? quest))
        {
            SimulateQuest(quest, sequence, step);
        }
    }

    public void SimulateQuest(Quest? quest, byte sequence, int step)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "SimulateQuest: {QuestId}", quest?.Id);
        if (quest != null)
        {
            SimulatedQuest = new(quest, sequence, step);
        }
        else
        {
            SimulatedQuest = null;
        }
    }

    public void StopSimulate()
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "StopSimulate");
        SimulatedQuest = null;
    }

    public void SetNextQuest(Quest? quest)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "NextQuest: {QuestId}", quest?.Id);
        if (quest != null)
        {
            NextQuest = new(quest);
        }
        else
        {
            NextQuest = null;
        }
    }

    public void SetGatheringQuest(Quest? quest)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "GatheringQuest: {QuestId}", quest?.Id);
        if (quest != null)
        {
            GatheringQuest = new(quest);
        }
        else
        {
            GatheringQuest = null;
        }
    }

    public void SetPendingQuest(QuestProgress? quest)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "PendingQuest: {QuestId}", quest?.Quest.Id);
        PendingQuest = quest;
    }

    protected override void UpdateCurrentTask()
    {
        if (_gameFunctions.IsOccupied() && !_gameFunctions.IsOccupiedWithCustomDeliveryNpc(CurrentQuest?.Quest))
        {
            return;
        }

        base.UpdateCurrentTask();
    }

    protected override void OnTaskComplete(ITask task)
    {
        if (task is WaitAtEnd.WaitQuestCompleted)
        {
            SimulatedQuest = null;
        }
    }

    protected override void OnNextStep(ILastTask task)
    {
        IncreaseStepCount(task.ElementId, task.Sequence, true);
    }

    public void Start(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"Q/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.Automatic;
        ExecuteNextStep();
    }

    public void StartGatheringQuest(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"GQ/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.GatheringOnly;
        ExecuteNextStep();
    }

    public void StartSingleQuest(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"SQ/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.SingleQuestA;
        ExecuteNextStep();
    }

    public void StartSingleStep(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"SS/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.Manual;
        ExecuteNextStep();
    }

    /// <summary>
    /// 登出時清空 <c>RedeemRewardItems</c> 的「已經試過的道具」表。
    /// </summary>
    /// <remarks>
    /// 那張表的鍵只有道具 id，而「用不掉」的理由多半是角色自己的（已經學過那個表情、背包滿、
    /// 等級不夠）——換角色之後前一個角色的結論不該繼續套用，否則新角色的獎勵道具會安靜地永遠不被使用。
    /// <para>
    /// 🔑 這是第二道保險，不是唯一一道：四個 <c>Start*</c> 進入點本來就各清一次，而登出會在
    /// <see cref="Update"/> 裡走到 <c>StopAllDueToConditionFailed("Logged out")</c> 把自動化停掉，
    /// 所以正常流程下換完角色一定會再經過一次 <c>Start*</c>。但那個不變式靠的是
    /// 「以後每一個新的自動化進入點都記得清」，這裡把它釘在真正失效的那一刻。
    /// </para>
    /// <para>
    /// ⚠️ 本 pin 的 <c>IClientState.Logout</c> 是 hook <c>AgentLobby</c> 的 vtable 得來的，
    /// Setup 當下拿不到 AgentLobby 就整個 session 都不會觸發（Dalamud 自己會記一行）。
    /// 所以這一條是補強，上面那層 <c>Start*</c> 的清空不要拿掉。
    /// </para>
    /// </remarks>
    private void ClearRedeemAttemptsOnLogout(int type, int code)
    {
        int cleared = RedeemRewardItems.ResetAttemptedItems();
        if (cleared > 0)
        {
            _logger.LogInformation(
                "Logout (type {LogoutType}, code {LogoutCode}): cleared {ClearedRedeemAttempts} attempted reward item(s)",
                type, code, cleared);
        }
    }

    private void ExecuteNextStep()
    {
        ClearTasksInternal();

        if (TryPickPriorityQuest())
        {
            LogOrDefer(LogLevel.Information, "Using priority quest over current quest");
        }

        (QuestSequence? seq, QuestStep? step, bool createTasks) = GetNextStep();
        if (CurrentQuest == null || seq == null)
        {
            if (CurrentQuestDetails?.Progress.Quest.Id is SatisfactionSupplyNpcId &&
                CurrentQuestDetails?.Progress.Sequence == 1 &&
                CurrentQuestDetails?.Progress.Step == 255 &&
                CurrentQuestDetails?.Type == ECurrentQuestType.Gathering)
            {
                LogOrDefer(LogLevel.Information, "Completed delivery quest");
                SetGatheringQuest(null);
                Stop("Gathering quest complete");
            }
            else
            {
                LogOrDefer(LogLevel.Warning,
                    "Could not retrieve next quest step, not doing anything [{QuestId}, {Sequence}, {Step}]",
                    CurrentQuest?.Quest.Id, CurrentQuest?.Sequence, CurrentQuest?.Step);
            }

            if (CurrentQuest == null || !createTasks)
            {
                return;
            }
        }

        // 🔴 傳 RunOrDefer 進去：這一支從 UpdateCurrentQuestLocked 進來時是持著 _progressLock 的，
        //    對 vnavmesh 打 Path.Stop、關自動前進、以及那幾行記錄會被收進延後清單、出鎖之後才做；
        //    狀態重設（ResetPathfinding／Destination／快照）仍然當場同步做。
        //    不在鎖裡呼叫時 RunOrDefer 就地執行 ⇒ 行為逐字不變。
        _movementController.Stop(RunOrDefer);
        _combatController.Stop("Execute next step", RunOrDefer);
        _gatheringController.Stop("Execute next step");

        try
        {
            // 🔴 傳 RunOrDefer 進去：工作清單仍然同步算好同步回來（回傳值語意不變），
            //    只有 CreateTasks 裡的 4 個聊天輸出與 5 行記錄被收進延後清單、出鎖之後才寫。
            foreach(ITask task in _taskCreator.CreateTasks(CurrentQuest.Quest, CurrentQuest.Sequence, seq, step,
                RunOrDefer))
            {
                if (SimulatedQuest != null)
                {
                    string repr = task.ToString() ?? "";
                    string[] SimSkip = ["Interact", "Action", "Emote", "Craft", "Unmount"];
                    if (repr.Contains('(') && SimSkip.Contains(repr[..repr.IndexOf('(')]) && step != null && step.TargetTerritoryId.Equals(step.TerritoryId))
                    {
                        LogOrDefer(LogLevel.Information, $"Skipping {repr} due to simulation");
                        continue;
                    }
                }
                _taskQueue.Enqueue(task);
            }

            ResetAutoRefreshState();
        }
        catch(Exception e)
        {
            LogOrDefer(LogLevel.Error, e, "Failed to create tasks");
            // 這個 catch 從 IPC 端點 Questionable.StartQuest／StartSingleQuest 可達
            //（QuestionableIpc.StartQuest -> StartSingleQuest -> ExecuteNextStep），而 IPC 跑在
            // 呼叫端外掛的執行緒上。見 ChatGuiExtensions：在 framework 執行緒上就地執行。
            RunOrDefer(() => _chatGui.PrintErrorOnFrameworkThread(_framework,
                "Failed to start next task sequence, please check /xllog for details.", CommandHandler.MessageTag,
                CommandHandler.TagColor));
            Stop("Tasks failed to create", true);
        }
    }

    public string ToStatString()
    {
        return _taskQueue.CurrentTaskExecutor?.CurrentTask is { } currentTask
            ? $"{currentTask} (+{_taskQueue.RemainingTasks.Count()})"
            : $"- (+{_taskQueue.RemainingTasks.Count()})";
    }

    public bool HasCurrentTaskExecutorMatching<T>([NotNullWhen(true)] out T? task)
    where T : class, ITaskExecutor
    {
        if (_taskQueue.CurrentTaskExecutor is T t)
        {
            task = t;
            return true;
        }
        else
        {
            task = null;
            return false;
        }
    }

    public bool HasCurrentTaskMatching<T>([NotNullWhen(true)] out T? task)
    where T : class, ITask
    {
        if (_taskQueue.CurrentTaskExecutor?.CurrentTask is T t)
        {
            task = t;
            return true;
        }
        else
        {
            task = null;
            return false;
        }
    }

    public void Skip(ElementId elementId, byte currentQuestSequence)
    {
        List<PendingLog> pending = [];
        List<PendingLog>? previousScope = _deferralScope;
        _deferralScope = pending;
        try
        {
            SkipLocked(elementId, currentQuestSequence, pending);
        }
        finally
        {
            _deferralScope = previousScope;
            EmitPending(pending);
        }
    }

    /// <summary><see cref="Skip"/> 持鎖的那一段。</summary>
    /// <remarks>
    /// 🔴 這裡面持有 <see cref="_progressLock"/>：要寫的東西用 <c>pending.Add</c> 或
    /// <see cref="LogOrDefer"/>，要做的副作用用 <see cref="RunOrDefer"/>。
    /// <para>
    /// 📌 這個鎖裡呼叫的 <c>Stop</c> 現在也會自己延後——它把 <c>BeginScope</c> 的
    /// <c>Stop/{label}</c> 前綴一起帶進延後的那一段裡重建，所以輸出的字一個都沒有變。
    /// </para>
    /// </remarks>
    private void SkipLocked(ElementId elementId, byte currentQuestSequence, List<PendingLog> pending)
    {
        lock(_progressLock)
        {
            if (_taskQueue.CurrentTaskExecutor?.CurrentTask is ISkippableTask)
            {
                _taskQueue.CurrentTaskExecutor = null;
            }
            else if (_taskQueue.CurrentTaskExecutor != null)
            {
                _taskQueue.CurrentTaskExecutor = null;
                while(_taskQueue.TryPeek(out ITask? task))
                {
                    _taskQueue.TryDequeue(out ITask? _);
                    if (task is ISkippableTask)
                    {
                        return;
                    }
                }

                if (_taskQueue.AllTasksComplete)
                {
                    Stop("Skip");
                    IncreaseStepCountLocked(elementId, currentQuestSequence, pending);
                }
            }
            else
            {
                Stop("SkipNx");
                IncreaseStepCountLocked(elementId, currentQuestSequence, pending);
            }
        }
    }

    public void SkipSimulatedTask()
    {
        _taskQueue.CurrentTaskExecutor = null;
    }

    public bool IsInterruptible()
    {
        if (AutomationType is EAutomationType.SingleQuestA or EAutomationType.SingleQuestB)
        {
            return false;
        }

        (QuestProgress Progress, ECurrentQuestType Type)? details = CurrentQuestDetails;
        if (details == null)
        {
            return false;
        }

        (QuestProgress currentQuest, ECurrentQuestType type) = details.Value;
        if (type != ECurrentQuestType.Normal || !currentQuest.Quest.Root.Interruptible || currentQuest.Sequence == 0)
        {
            return false;
        }

        if (ManualPriorityQuests.Contains(currentQuest.Quest))
        {
            return false;
        }

        // "ifrit bleeds, we can kill it" isn't listed as priority quest, as we accept it during the MSQ 'Moving On'
        // the rest are priority quests, but that's fine here
        if (QuestData.HardModePrimals.Contains(currentQuest.Quest.Id))
        {
            return false;
        }

        if (currentQuest.Quest.Info.AlliedSociety != EAlliedSociety.None)
        {
            return false;
        }

        QuestSequence? currentSequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
        if (currentQuest.Step > 0)
        {
            return false;
        }

        QuestStep? currentStep = currentSequence?.FindStep(currentQuest.Step);
        return currentStep?.AetheryteShortcut != null &&
               (currentStep.SkipConditions?.AetheryteShortcutIf?.QuestsCompleted.Count ?? 0) == 0 &&
               (currentStep.SkipConditions?.AetheryteShortcutIf?.QuestsAccepted.Count ?? 0) == 0;
    }

    public bool TryPickPriorityQuest()
    {
        if (!IsInterruptible() || NextQuest != null || GatheringQuest != null || SimulatedQuest != null)
        {
            return false;
        }

        ElementId? priorityQuestId = _questFunctions.NextPriorityQuestsThatCanBeAccepted
            .Where(x => x.IsAvailable)
            .Select(x => x.QuestId)
            .FirstOrDefault();
        if (priorityQuestId == null)
        {
            return false;
        }

        // don't start a second priority quest until the first one is resolved
        if (StartedQuest != null && priorityQuestId == StartedQuest.Quest.Id)
        {
            return false;
        }

        if (_questRegistry.TryGetQuest(priorityQuestId, out Quest? quest))
        {
            SetNextQuest(quest);
            return true;
        }

        return false;
    }

    public void ImportQuestPriority(List<ElementId> questElements)
    {
        foreach(ElementId elementId in questElements)
        {
            if (_questRegistry.TryGetQuest(elementId, out Quest? quest) && !ManualPriorityQuests.Contains(quest))
            {
                ManualPriorityQuests.Add(quest);
            }
        }
    }
    public string ExportQuestPriority()
    {
        return string.Join(ClipboardSeparator, ManualPriorityQuests.Select(x => x.Id.ToString()));
    }

    public void ClearQuestPriority()
    {
        ManualPriorityQuests.Clear();
    }

    public bool AddQuestPriority(ElementId elementId)
    {
        if (_questRegistry.TryGetQuest(elementId, out Quest? quest) &&
            !ManualPriorityQuests.Contains(quest) &&
            (_questFunctions.IsDailyAlliedSocietyQuest((QuestId)elementId) || !_questFunctions.IsQuestComplete(elementId))
            )
        {
            ManualPriorityQuests.Add(quest);
        }
        return true;
    }

    public bool RemoveQuestPriority(ElementId elementId)
    {
        if (_questRegistry.TryGetQuest(elementId, out Quest? quest) && ManualPriorityQuests.Contains(quest))
        {
            ManualPriorityQuests.Remove(quest);
        }
        return true;
    }

    public bool InsertQuestPriority(int index, ElementId elementId)
    {
        try
        {
            if (_questRegistry.TryGetQuest(elementId, out Quest? quest) && !ManualPriorityQuests.Contains(quest))
            {
                ManualPriorityQuests.Insert(index, quest);
            }
            return true;
        }
        catch(Exception e)
        {
            _logger.LogError(e, "Failed to insert quest in priority list");
            // IPC 端點 Questionable.InsertQuestPriority 可達，見 ChatGuiExtensions。
            _chatGui.PrintErrorOnFrameworkThread(_framework, "Failed to insert quest in priority list, please check /xllog for details.", CommandHandler.MessageTag, CommandHandler.TagColor);
            return false;
        }
    }

    public bool WasLastTaskUpdateWithin(TimeSpan timeSpan)
    {
        _logger.LogInformation("Last update: {Update}", _lastTaskUpdate);
        return IsRunning || DateTime.Now <= _lastTaskUpdate.Add(timeSpan);
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (_taskQueue.CurrentTaskExecutor is IConditionChangeAware conditionChangeAware)
        {
            conditionChangeAware.OnConditionChange(flag, value);
        }
    }

    private void OnNormalToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        _gatheringController.OnNormalToast(message);
    }

    protected override void HandleInterruption(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        if (AutomationType == EAutomationType.Manual)
        {
            return;
        }

        base.HandleInterruption(sender, e);
    }

    public bool StartGathering(uint npcId, uint itemId, Job classJob, int quantity = 1, ushort collectability = 0)
    {
        if (itemId > 1_000_000)
        {
            itemId -= 1_000_000;
        }

        if (itemId >= 500_000)
        {
            itemId -= 500_000;
        }

        SatisfactionSupplyInfo info = (SatisfactionSupplyInfo)_questData.GetAllByIssuerDataId(npcId)
            .Single(x => x is SatisfactionSupplyInfo);
        if (_questRegistry.TryGetQuest(info.QuestId, out Quest? quest))
        {
            QuestSequence sequence = quest.FindSequence(0)!;

            QuestStep switchClassStep = sequence.Steps.Single(x => x.InteractionType == EInteractionType.SwitchClass);
            switchClassStep.TargetClass = classJob switch
            {
                Job.MIN => EExtendedClassJob.Miner,
                Job.BTN => EExtendedClassJob.Botanist,
                var _ => throw new ArgumentOutOfRangeException(nameof(classJob), classJob, null)
            };

            QuestStep gatherStep = sequence.Steps.Single(x => x.InteractionType == EInteractionType.Gather);
            gatherStep.ItemsToGather =
            [
                new()
                {
                    ItemId = itemId,
                    ItemCount = quantity,
                    Collectability = collectability
                }
            ];
            SetGatheringQuest(quest);
            StartGatheringQuest("SatisfactionSupply prepare gathering");
            return true;
        }
        else
        {
            // IPC 端點 Questionable.StartGathering／StartGatheringComplex 可達，見 ChatGuiExtensions。
            _chatGui.PrintErrorOnFrameworkThread(_framework, $"No associated quest ({info.QuestId}).", "Questionable");
            return false;
        }
    }

    public override void Dispose()
    {
        _clientState.Logout -= ClearRedeemAttemptsOnLogout;
        _toastGui.ErrorToast -= OnErrorToast;
        _toastGui.Toast -= OnNormalToast;
        _condition.ConditionChange -= OnConditionChange;
        base.Dispose();
    }

    public sealed class QuestProgress
    {
        public QuestProgress(Quest quest, byte sequence = 0, int step = 0)
        {
            Quest = quest;
            SetSequence(sequence, step);
        }
        public Quest Quest { get; }
        public byte Sequence { get; private set; }
        public int Step { get; private set; }
        public StepProgress StepProgress { get; private set; } = new(DateTime.Now);

        public void SetSequence(byte sequence, int step = 0)
        {
            Sequence = sequence;
            SetStep(step);
        }

        public void SetStep(int step)
        {
            Step = step;
            StepProgress = new(DateTime.Now);
        }

        public void IncreasePointMenuCounter()
        {
            StepProgress = StepProgress with
            {
                PointMenuCounter = StepProgress.PointMenuCounter + 1
            };
        }
    }

    public sealed record StepProgress
    (
        DateTime StartedAt,
        int PointMenuCounter = 0);
}
