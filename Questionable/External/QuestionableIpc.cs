using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECommons.ExcelServices;
using JetBrains.Annotations;
using Lumina.Text.ReadOnly;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Windows;
using Questionable.Windows.QuestComponents;
using Questionable.Windows.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.External;

/// <remarks>
/// 🔴 <b>這裡的每一個端點都跑在呼叫端外掛的執行緒上</b>（CallGate＝直接方法呼叫），
/// 所以 21 支全部包上 <see cref="IpcFrameworkGate"/>：已經在主執行緒上呼叫時就地執行、
/// 行為逐字不變，從別的執行緒進來才會被交回主執行緒。逐支的理由：
/// <list type="bullet">
/// <item><c>StartQuest</c>／<c>StartSingleQuest</c>／<c>StartGathering</c>／
/// <c>StartGatheringComplex</c>／<c>Stop</c>：同步走到 <c>QuestController.ExecuteNextStep</c>
/// 與 <c>Stop</c>，那裡有 <c>TaskQueue._tasks</c>（裸 <c>List</c>）的寫入、
/// <c>_objectTable[0]</c> 的原生讀取、以及 <c>StatusManager.ExecuteStatusOff</c>。</item>
/// <item><c>ImportQuestPriority</c>／<c>AddQuestPriority</c>／<c>InsertQuestPriority</c>／
/// <c>ClearQuestPriority</c>／<c>ExportQuestPriority</c>：碰 <c>ManualPriorityQuests</c>，
/// 那是裸 <c>List</c>，而 UI 每幀在繪製執行緒上迭代它。</item>
/// <item><c>IsQuestLocked</c>／<c>IsQuestComplete</c>／<c>IsReadyToAcceptQuest</c>／
/// <c>IsQuestAccepted</c>／<c>IsQuestUnobtainable</c>／<c>GetCurrentQuestId</c>／
/// <c>GetCurrentStepData</c>／<c>GetCurrentlyActiveEventQuests</c>：查詢，但同步讀
/// <c>QuestManager.Instance()-&gt;</c>（<c>GetCurrentQuestId</c> 與 <c>GetCurrentStepData</c>
/// 經由 <c>CurrentQuestDetails</c> -&gt; <c>IsReadyToAcceptQuest</c>）。</item>
/// <item><c>IsRunning</c>：讀 <c>TaskQueue</c> 的裸 <c>List</c> 計數。</item>
/// <item><c>RedoLookup</c>／<c>RedoLookupIndex</c>：讀 Lumina 的 <c>ExcelSheet</c>
/// （內部有列快取，沒有保證執行緒安全）。</item>
/// </list>
/// <para>
/// 📌 <b>回傳語意沒有改</b>：閘門逾時時回的「不可用」值，每一個都是該端點原本失敗路徑上
/// 就會回的值（<c>IsQuestLocked</c> 回 <see langword="true"/>＝當作鎖著，其餘查詢回
/// <see langword="false"/>／<see langword="null"/>／空字串）。⚠️ 唯一的例外是四支
/// 「優先任務」端點：它們原本無論如何都回 <see langword="true"/>，逾時時會回
/// <see langword="false"/>——那才是誠實的「沒做到」。
/// </para>
/// </remarks>
internal sealed class QuestionableIpc : IDisposable
{
    private const string IpcIsRunning = "Questionable.IsRunning";
    private const string IpcGetCurrentQuestId = "Questionable.GetCurrentQuestId";
    private const string IpcGetCurrentStepData = "Questionable.GetCurrentStepData";
    private const string IpcGetCurrentlyActiveEventQuests = "Questionable.GetCurrentlyActiveEventQuests";
    private const string IpcStartQuest = "Questionable.StartQuest";
    private const string IpcStartSingleQuest = "Questionable.StartSingleQuest";
    private const string IpcIsQuestLocked = "Questionable.IsQuestLocked";
    private const string IpcIsQuestComplete = "Questionable.IsQuestComplete";
    private const string IpcIsReadyToAcceptQuest = "Questionable.IsReadyToAcceptQuest";
    private const string IpcIsQuestAccepted = "Questionable.IsQuestAccepted";
    private const string IpcIsQuestUnobtainable = "Questionable.IsQuestUnobtainable";
    private const string IpcImportQuestPriority = "Questionable.ImportQuestPriority";
    private const string IpcClearQuestPriority = "Questionable.ClearQuestPriority";
    private const string IpcAddQuestPriority = "Questionable.AddQuestPriority";
    private const string IpcInsertQuestPriority = "Questionable.InsertQuestPriority";
    private const string IpcExportQuestPriority = "Questionable.ExportQuestPriority";
    private const string IpcStartGathering = "Questionable.StartGathering";
    private const string IpcStartGatheringComplex = "Questionable.StartGatheringComplex";
    private const string IpcStop = "Questionable.Stop";
    private const string IpcRedoLookup = "Questionable.RedoLookup";
    private const string IpcRedoLookupIndex = "Questionable.RedoLookupIndex";
    private readonly ICallGateProvider<string, bool> _addQuestPriority;
    private readonly ICallGateProvider<bool> _clearQuestPriority;
    private readonly ICallGateProvider<string> _exportQuestPriority;
    private readonly ICallGateProvider<List<string>> _getCurrentlyActiveEventQuests;
    private readonly ICallGateProvider<string?> _getCurrentQuestId;
    private readonly ICallGateProvider<StepData?> _getCurrentStepData;
    private readonly ICallGateProvider<string, bool> _importQuestPriority;
    private readonly ICallGateProvider<int, string, bool> _insertQuestPriority;
    private readonly ICallGateProvider<string, bool> _isQuestAccepted;
    private readonly ICallGateProvider<string, bool> _isQuestComplete;
    private readonly ICallGateProvider<string, bool> _isQuestLocked;
    private readonly ICallGateProvider<string, bool> _isQuestUnobtainable;
    private readonly ICallGateProvider<string, bool> _isReadyToAcceptQuest;

    private readonly IpcFrameworkGate _gate;

    private readonly ICallGateProvider<bool> _isRunning;
    private readonly ILogger<QuestionableIpc> _logger;

    private readonly QuestController _questController;
    private readonly QuestFunctions _questFunctions;
    private readonly QuestRegistry _questRegistry;
    private readonly ICallGateProvider<uint, string> _redoLookup;
    private readonly ICallGateProvider<uint, Tuple<string, int>> _redoLookupIndex;
    private readonly RedoUtil _redoUtil;
    private readonly ICallGateProvider<uint, uint, byte, int, bool> _startGathering;
    private readonly ICallGateProvider<uint, uint, byte, int, ushort, bool> _startGatheringComplex;
    private readonly ICallGateProvider<string, bool> _startQuest;
    private readonly ICallGateProvider<string, bool> _startSingleQuest;
    private readonly ICallGateProvider<string, bool> _stop;

    public QuestionableIpc(
        QuestController questController,
        EventInfoComponent eventInfoComponent,
        QuestRegistry questRegistry,
        QuestFunctions questFunctions,
        PriorityWindow priorityWindow,
        ILogger<QuestionableIpc> logger,
        IDalamudPluginInterface pluginInterface,
        IpcFrameworkGate gate)
    {
        _questController = questController;
        _questRegistry = questRegistry;
        _questFunctions = questFunctions;
        _logger = logger;
        _gate = gate;

        _isRunning = pluginInterface.GetIpcProvider<bool>(IpcIsRunning);
        _isRunning.RegisterFunc(() => _gate.Get(IpcIsRunning,
            () => questController.AutomationType != QuestController.EAutomationType.Manual || questController.IsRunning,
            false));

        _getCurrentQuestId = pluginInterface.GetIpcProvider<string?>(IpcGetCurrentQuestId);
        _getCurrentQuestId.RegisterFunc(() => _gate.Get<string?>(IpcGetCurrentQuestId,
            () => questController.CurrentQuest?.Quest.Id.ToString(), null));

        _getCurrentStepData = pluginInterface.GetIpcProvider<StepData?>(IpcGetCurrentStepData);
        _getCurrentStepData.RegisterFunc(() => _gate.Get<StepData?>(IpcGetCurrentStepData, GetStepData, null));

        _getCurrentlyActiveEventQuests =
            pluginInterface.GetIpcProvider<List<string>>(IpcGetCurrentlyActiveEventQuests);
        _getCurrentlyActiveEventQuests.RegisterFunc(() => _gate.Get<List<string>>(IpcGetCurrentlyActiveEventQuests,
            () => [.. eventInfoComponent.GetCurrentlyActiveEventQuests().Select(q => q.ToString())], []));

        _startQuest = pluginInterface.GetIpcProvider<string, bool>(IpcStartQuest);
        _startQuest.RegisterFunc(questId => _gate.Get(IpcStartQuest, () => StartQuest(questId, false), false));

        _startSingleQuest = pluginInterface.GetIpcProvider<string, bool>(IpcStartSingleQuest);
        _startSingleQuest.RegisterFunc(questId =>
            _gate.Get(IpcStartSingleQuest, () => StartQuest(questId, true), false));

        _isQuestLocked = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestLocked);
        // ⚠️ 不可用時回 true：與這一支自己「查不到任務就當作鎖著」的既有預設一致。
        _isQuestLocked.RegisterFunc(questId => _gate.Get(IpcIsQuestLocked, () => IsQuestLocked(questId), true));

        _isQuestComplete = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestComplete);
        _isQuestComplete.RegisterFunc(questId =>
            _gate.Get(IpcIsQuestComplete, () => IsQuestComplete(questId), false));

        _isReadyToAcceptQuest = pluginInterface.GetIpcProvider<string, bool>(IpcIsReadyToAcceptQuest);
        _isReadyToAcceptQuest.RegisterFunc(questId =>
            _gate.Get(IpcIsReadyToAcceptQuest, () => IsReadyToAcceptQuest(questId), false));

        _isQuestAccepted = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestAccepted);
        _isQuestAccepted.RegisterFunc(questId =>
            _gate.Get(IpcIsQuestAccepted, () => IsQuestAccepted(questId), false));

        _isQuestUnobtainable = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestUnobtainable);
        _isQuestUnobtainable.RegisterFunc(questId =>
            _gate.Get(IpcIsQuestUnobtainable, () => IsQuestUnobtainable(questId), false));

        _importQuestPriority = pluginInterface.GetIpcProvider<string, bool>(IpcImportQuestPriority);
        _importQuestPriority.RegisterFunc(encoded =>
            _gate.Get(IpcImportQuestPriority, () => ImportQuestPriority(encoded), false));

        _addQuestPriority = pluginInterface.GetIpcProvider<string, bool>(IpcAddQuestPriority);
        _addQuestPriority.RegisterFunc(questId =>
            _gate.Get(IpcAddQuestPriority, () => AddQuestPriority(questId), false));

        _clearQuestPriority = pluginInterface.GetIpcProvider<bool>(IpcClearQuestPriority);
        _clearQuestPriority.RegisterFunc(() => _gate.Get(IpcClearQuestPriority, ClearQuestPriority, false));

        _insertQuestPriority = pluginInterface.GetIpcProvider<int, string, bool>(IpcInsertQuestPriority);
        _insertQuestPriority.RegisterFunc((index, questId) =>
            _gate.Get(IpcInsertQuestPriority, () => InsertQuestPriority(index, questId), false));

        _exportQuestPriority = pluginInterface.GetIpcProvider<string>(IpcExportQuestPriority);
        _exportQuestPriority.RegisterFunc(() =>
            _gate.Get(IpcExportQuestPriority, priorityWindow.EncodeQuestPriority, string.Empty));

        _startGathering = pluginInterface.GetIpcProvider<uint, uint, byte, int, bool>(IpcStartGathering);
        _startGathering.RegisterFunc((npcId, itemId, classJob, quantity) =>
            _gate.Get(IpcStartGathering, () => StartGathering(npcId, itemId, classJob, quantity), false));

        _startGatheringComplex = pluginInterface.GetIpcProvider<uint, uint, byte, int, ushort, bool>(IpcStartGatheringComplex);
        _startGatheringComplex.RegisterFunc((npcId, itemId, classJob, quantity, collectability) =>
            _gate.Get(IpcStartGatheringComplex,
                () => StartGatheringComplex(npcId, itemId, classJob, quantity, collectability), false));

        _stop = pluginInterface.GetIpcProvider<string, bool>(IpcStop);
        _stop.RegisterFunc(label => _gate.Get(IpcStop, () => Stop(label), false));

        _redoUtil = new();

        _redoLookup = pluginInterface.GetIpcProvider<uint, string>(IpcRedoLookup);
        _redoLookup.RegisterFunc(questId =>
            _gate.Get(IpcRedoLookup, () => RedoLookup(questId), string.Empty));

        _redoLookupIndex = pluginInterface.GetIpcProvider<uint, Tuple<string, int>>(IpcRedoLookupIndex);
        _redoLookupIndex.RegisterFunc(questId =>
            _gate.Get(IpcRedoLookupIndex, () => RedoLookupIndex(questId), new Tuple<string, int>(string.Empty, -1)));
    }

    public void Dispose()
    {
        _isRunning.UnregisterFunc();
        _getCurrentQuestId.UnregisterFunc();
        _getCurrentStepData.UnregisterFunc();
        _getCurrentlyActiveEventQuests.UnregisterFunc();
        _startQuest.UnregisterFunc();
        _startSingleQuest.UnregisterFunc();
        _isQuestLocked.UnregisterFunc();
        _isQuestComplete.UnregisterFunc();
        _isReadyToAcceptQuest.UnregisterFunc();
        _isQuestAccepted.UnregisterFunc();
        _isQuestUnobtainable.UnregisterFunc();
        _importQuestPriority.UnregisterFunc();
        _addQuestPriority.UnregisterFunc();
        _clearQuestPriority.UnregisterFunc();
        _insertQuestPriority.UnregisterFunc();
        _exportQuestPriority.UnregisterFunc();
        _startGathering.UnregisterFunc();
        _startGatheringComplex.UnregisterFunc();
        _stop.UnregisterFunc();
        _redoLookup.UnregisterFunc();
        _redoLookupIndex.UnregisterFunc();
    }

    private bool StartQuest(string questId, bool single)
    {
        _logger.LogDebug($"StartQuest({questId},{single})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            _questRegistry.TryGetQuest(elementId, out Quest? quest))
        {
            _questController.SetNextQuest(quest);
            if (single)
            {
                _questController.StartSingleQuest("IPCQuestSelection");
            }
            else
            {
                _questController.Start("IPCQuestSelection");
            }
            return true;
        }

        return false;
    }

    private StepData? GetStepData()
    {
        _logger.LogDebug("GetStepData()");
        QuestController.QuestProgress? progress = _questController.CurrentQuest;
        if (progress == null)
        {
            return null;
        }

        string questId = progress.Quest.Id.ToString();
        if (string.IsNullOrEmpty(questId))
        {
            return null;
        }

        QuestStep? step = progress.Quest.FindSequence(progress.Sequence)?.FindStep(progress.Step);
        if (step == null)
        {
            return null;
        }

        return new()
        {
            QuestId = questId,
            Sequence = progress.Sequence,
            Step = progress.Step,
            InteractionType = step.InteractionType.ToString(),
            Position = step.Position,
            TerritoryId = step.TerritoryId
        };
    }

    private bool IsQuestLocked(string questId)
    {
        _logger.LogDebug($"IsQuestLocked({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            _questRegistry.TryGetQuest(elementId, out Quest? _))
        {
            return _questFunctions.IsQuestLocked(elementId);
        }

        return true;
    }

    private bool IsQuestComplete(string questId)
    {
        _logger.LogDebug($"IsQuestComplete({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null)
        {
            return _questFunctions.IsQuestComplete(elementId);
        }
        return false;
    }

    private bool IsReadyToAcceptQuest(string questId)
    {
        _logger.LogDebug($"IsReadyToAcceptQuest({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null)
        {
            return _questFunctions.IsReadyToAcceptQuest(elementId);
        }
        return false;
    }

    private bool IsQuestAccepted(string questId)
    {
        _logger.LogDebug($"IsQuestAccepted({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null)
        {
            return _questFunctions.IsQuestAccepted(elementId);
        }
        return false;
    }

    private bool IsQuestUnobtainable(string questId)
    {
        _logger.LogDebug($"IsQuestUnobtainable({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null)
        {
            return _questFunctions.IsQuestUnobtainable(elementId);
        }
        return false;
    }

    private bool ImportQuestPriority(string encodedQuestPriority)
    {
        _logger.LogDebug($"ImportQuestPriority({encodedQuestPriority})");
        List<ElementId> questElements = PriorityWindow.DecodeQuestPriority(encodedQuestPriority);
        _questController.ImportQuestPriority(questElements);
        return true;
    }

    private bool ClearQuestPriority()
    {
        _logger.LogDebug("ClearQuestPriority()");
        _questController.ClearQuestPriority();
        return true;
    }

    private bool AddQuestPriority(string questId)
    {
        _logger.LogDebug($"AddQuestPriority({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            _questRegistry.IsKnownQuest(elementId))
        {
            return _questController.AddQuestPriority(elementId);
        }

        return true;
    }

    private bool InsertQuestPriority(int index, string questId)
    {
        _logger.LogDebug($"InsertQuestPriority({index},{questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            _questRegistry.IsKnownQuest(elementId))
        {
            return _questController.InsertQuestPriority(index, elementId);
        }

        return true;
    }

    private bool StartGathering(uint npcId, uint itemId, byte classJob, int quantity)
    {
        return StartGatheringComplex(npcId, itemId, classJob, quantity);
    }

    private bool StartGatheringComplex(uint npcId, uint itemId, byte classJob = ((byte)Job.MIN), int quantity = 1, ushort collectability = 0)
    {
        _logger.LogDebug($"StartGatheringComplex({npcId},{itemId},{classJob},{quantity},{collectability})");
        return _questController.StartGathering(npcId, itemId, (Job)classJob, quantity, collectability);
    }

    private bool Stop(string label)
    {
        _logger.LogDebug($"Stop({label})");
        _questController.StopAllDueToConditionFailed($"IPC: {label}");
        return true;
    }

    private string RedoLookup(uint questId)
    {
        if (questId >= 131072)
        {
            return "";
        }
        if (questId >= 65536)
        {
            questId -= 65536;
        }
        return _redoUtil.GetChapter(questId).Item1.ToString();
    }

    private Tuple<string, int> RedoLookupIndex(uint questId)
    {
        if (questId >= 131072)
        {
            return new("", -1);
        }
        if (questId >= 65536)
        {
            questId -= 65536;
        }
        Tuple<ReadOnlySeString, int> outp = _redoUtil.GetChapter(questId);
        return new(outp.Item1.ToString(), outp.Item2);
    }

    [UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
    public sealed class StepData
    {
        public required string QuestId { get; init; }
        public required byte Sequence { get; init; }
        public required int Step { get; init; }
        public required string InteractionType { get; init; }
        public required Vector3? Position { get; init; }
        public required uint TerritoryId { get; init; }
    }
}
