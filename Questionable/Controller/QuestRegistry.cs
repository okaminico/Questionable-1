using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.QuestPaths;
using Questionable.Utils;
using Questionable.Validation;
using Questionable.Validation.Validators;
using static Questionable.Utils.CacheUtils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
namespace Questionable.Controller;

internal sealed class QuestRegistry
{
    private readonly IChatGui _chatGui;
    private readonly IDataManager _dataManager;

    /// <summary>只用來把聊天輸出釘回 framework 執行緒，見 <see cref="ChatGuiExtensions"/>。</summary>
    private readonly IFramework _framework;
    private readonly JsonSchemaValidator _jsonSchemaValidator;
    private readonly ILogger<QuestRegistry> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly QuestData _questData;
    private readonly QuestValidator _questValidator;

    private readonly ICallGateProvider<object> _reloadDataIpc;
    private readonly TerritoryData _territoryData;

    /// <summary>
    /// 「解析」那一段的序列化閘門：<see cref="PrepareReload"/> 是唯一會取它的地方。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這是一把只在「重新載入」這條路徑上會被取的鎖</b>，與每個 framework tick 都會被拿去的
    /// <c>QuestController._progressLock</c> 是兩把不同的鎖 —— 整個改動的重點就是不要再用後者
    /// 去包住檔案列舉與 JSON 解析。
    /// <para>
    /// 需要它的理由有兩個：①卡住重試計時器（framework 執行緒）與使用者按下重新載入（繪製執行緒）
    /// 可以同時進來，不擋的話會同時解析兩份；②解析途中會寫進共用的
    /// <see cref="JsonSchemaValidator"/>（裸 <c>Dictionary</c>），必須只有一個寫入者。
    /// </para>
    /// <para>
    /// 🔴 <b>取著它的時候不寫記錄、不輸出聊天、不打 IPC</b>：要寫的東西一律加進
    /// <see cref="ReloadBatch.PendingLogs"/>，由 <see cref="EmitReloadSideEffects"/> 在所有鎖都
    /// 放掉之後才寫出去。
    /// </para>
    /// </remarks>
    private readonly object _prepareGate = new();

    /// <summary>
    /// 只保護「把準備好的登錄換上去」那幾行參照指派；持有時間是常數，裡面沒有任何 I/O。
    /// </summary>
    private readonly object _swapGate = new();

    /// <summary>下一批要用的編號，由 <see cref="PrepareReload"/> 以 <see cref="Interlocked"/> 遞增。</summary>
    private int _reloadSequence;

    /// <summary>已經公開上去的批次編號；編號比它舊的批次一律丟掉，不覆蓋比較新的結果。</summary>
    private int _publishedSequence;

    private Dictionary<uint, (ElementId QuestId, QuestStep Step)> _contentFinderConditionIds = [];

    private List<(uint ContentFinderConditionId, ElementId QuestId, int Sequence)>
        _lowPriorityContentFinderConditionQuests = [];

    private Dictionary<ElementId, Quest> _quests = [];

    public QuestRegistry(
        IDalamudPluginInterface pluginInterface,
        QuestData questData,
        QuestValidator questValidator,
        JsonSchemaValidator jsonSchemaValidator,
        ILogger<QuestRegistry> logger,
        TerritoryData territoryData,
        IDataManager dataManager,
        IChatGui chatGui,
        IFramework framework)
    {
        _pluginInterface = pluginInterface;
        _questData = questData;
        _questValidator = questValidator;
        _jsonSchemaValidator = jsonSchemaValidator;
        _logger = logger;
        _territoryData = territoryData;
        _chatGui = chatGui;
        _dataManager = dataManager;
        _framework = framework;
        _reloadDataIpc = _pluginInterface.GetIpcProvider<object>("Questionable.ReloadData");
    }

    public IEnumerable<Quest> AllQuests => _quests.Values;
    private CachedValue<int> _count = new(ttlSeconds: 1);
    public int Count => _count.Get(() => _quests.Count(x => !x.Value.Root.Disabled));
    /// <summary>
    /// 內建任務路線中,因為目前遊戲版本的 Quest 表查不到對應任務而沒有載入的條數。
    /// </summary>
    public int SkippedQuestCount { get; private set; }

    public int ValidationIssueCount => _questValidator.IssueCount;
    public int ValidationErrorCount => _questValidator.ErrorCount;

    public IReadOnlyList<(uint ContentFinderConditionId, ElementId QuestId, int Sequence)>
        LowPriorityContentFinderConditionQuests => _lowPriorityContentFinderConditionQuests;

    public event EventHandler? Reloaded;

    /// <summary>
    /// 解析階段產生的一行輸出。<c>ILogger</c> 最後落到 Dalamud 的 Serilog sink，那邊自己有鎖、
    /// 還會做檔案 I/O ——在鎖裡呼叫等於把鎖的持有時間綁在磁碟上。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意是純資料</b>：真正的寫入放在 <see cref="EmitReloadSideEffects"/>，
    /// 這樣「持鎖時可達的成員」裡不存在任何會寫記錄的東西。
    /// <para>
    /// <paramref name="ChatMessage"/> 不是 <see langword="null"/> 時代表這一則是要印到聊天視窗的，
    /// 兩種放在同一個清單裡，原本的先後順序才保得住。
    /// </para>
    /// </remarks>
    internal readonly record struct PendingLog(
        LogLevel Level,
        string Template,
        object?[] Args,
        Exception? Exception,
        string? ChatMessage);

    /// <summary>
    /// <see cref="PrepareReload"/> 準備好、但還沒公開出去的一份新登錄。
    /// </summary>
    /// <remarks>
    /// 🔴 在 <see cref="PublishReload"/> 之前，這裡面的集合<b>只有正在解析的那一條執行緒看得到</b>
    /// ⇒ 解析可以完全在鎖外面做，公開的動作只剩幾行參照指派。
    /// </remarks>
    internal sealed class ReloadBatch
    {
        public required int Sequence { get; init; }

        public Dictionary<ElementId, Quest> Quests { get; } = [];

        public Dictionary<uint, (ElementId QuestId, QuestStep Step)> ContentFinderConditionIds { get; } = [];

        public List<(uint ContentFinderConditionId, ElementId QuestId, int Sequence)>
            LowPriorityContentFinderConditionQuests { get; } = [];

        public int SkippedQuestCount { get; set; }

        public List<PendingLog> PendingLogs { get; } = [];
    }

    /// <summary>
    /// 重新載入：解析 → 換上去 → 發表副作用。
    /// </summary>
    /// <remarks>
    /// 📌 這一支是給「手上沒有別的鎖要顧」的呼叫端用的（外掛初始化）。
    /// <see cref="QuestController"/> 改成分開呼叫三段，好把中間那段（而且只有那一段）
    /// 放進它自己的 <c>_progressLock</c> 裡。
    /// </remarks>
    public void Reload()
    {
        ReloadBatch batch = PrepareReload();
        bool published = false;
        try
        {
            published = PublishReload(batch);
        }
        finally
        {
            EmitReloadSideEffects(batch, published);
        }
    }

    /// <summary>
    /// 重新載入的第一段：列舉檔案、解析 JSON、排出驗證工作。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>結果只寫進回傳的那個 <see cref="ReloadBatch"/></b>，已經公開的登錄一個位元組都沒動
    /// ⇒ 呼叫端<b>必須</b>在自己的鎖外面呼叫它。這一段就是原本被 <c>_progressLock</c> 包住的
    /// 那一整片檔案系統列舉與 JSON 解析。
    /// </remarks>
    public ReloadBatch PrepareReload()
    {
        lock(_prepareGate)
        {
            ReloadBatch batch = new() { Sequence = Interlocked.Increment(ref _reloadSequence) };

            _questValidator.Reset(batch.Sequence);

            LoadQuestsFromAssembly(batch);
            LoadQuestsFromProjectDirectory(batch);

            try
            {
                LoadFromDirectory(batch,
                    new(Path.Combine(_pluginInterface.ConfigDirectory.FullName, "Quests")),
                    Quest.ESource.UserDirectory);
            }
            catch(Exception e)
            {
                batch.PendingLogs.Add(new PendingLog(LogLevel.Error,
                    "Failed to load all quests from user directory (some may have been successfully loaded)",
                    [], e, null));
            }

            LoadCfcIds(batch);
            ValidateQuests(batch);
            return batch;
        }
    }

    /// <summary>
    /// 重新載入的第二段：把準備好的登錄原子替換上去。<b>只有參照指派、沒有任何 I/O</b>，
    /// 所以可以放在呼叫端的鎖裡面。
    /// </summary>
    /// <returns>
    /// <see langword="false"/>＝這一批已經過期（我們解析的期間有更新的一批公開過了），整批丟掉。
    /// </returns>
    /// <remarks>
    /// 📌 讀取端（UI、IPC、任務引擎）本來就沒有任何一個會去拿 <c>_progressLock</c>，
    /// 所以原本那個「先 <c>Clear()</c> 再一條一條填回去」的作法對它們是<b>完全沒有保護</b>的：
    /// 迭代中的 <c>AllQuests</c> 會擲 <c>InvalidOperationException</c>、查表會查到半空的登錄。
    /// 換成整份替換之後，它們看到的不是舊的就是新的，沒有中間狀態。
    /// </remarks>
    public bool PublishReload(ReloadBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        lock(_swapGate)
        {
            if (batch.Sequence <= _publishedSequence)
            {
                return false;
            }

            _publishedSequence = batch.Sequence;
            _quests = batch.Quests;
            _contentFinderConditionIds = batch.ContentFinderConditionIds;
            _lowPriorityContentFinderConditionQuests = batch.LowPriorityContentFinderConditionQuests;
            SkippedQuestCount = batch.SkippedQuestCount;
            return true;
        }
    }

    /// <summary>
    /// 重新載入的第三段：寫記錄、發 <see cref="Reloaded"/> 事件、廣播 IPC。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>必須在所有鎖都放掉之後才呼叫。</b><see cref="Reloaded"/> 的訂閱端
    /// （<c>GatheringPointRegistry</c>）會再做一次完整的檔案系統列舉，而 <c>SendMessage</c> 會
    /// <b>同步</b>跑每一個訂閱它的外掛的處理常式 —— 在鎖裡呼叫等於把別的外掛的鎖排在我們的鎖後面。
    /// </remarks>
    public void EmitReloadSideEffects(ReloadBatch batch, bool published)
    {
        ArgumentNullException.ThrowIfNull(batch);

        foreach(PendingLog entry in batch.PendingLogs)
        {
            if (entry.ChatMessage is { } chatMessage)
            {
                _chatGui.PrintErrorOnFrameworkThread(_framework, chatMessage, CommandHandler.MessageTag,
                    CommandHandler.TagColor);
            }
            else
            {
                _logger.Log(entry.Level, entry.Exception, entry.Template, entry.Args);
            }
        }

        batch.PendingLogs.Clear();

        if (!published)
        {
            _logger.LogInformation(
                "Discarding quest registry reload #{Sequence}, a newer reload has already been published",
                batch.Sequence);
            return;
        }

        Reloaded?.Invoke(this, EventArgs.Empty);
        try
        {
            _reloadDataIpc.SendMessage();
        }
        catch(Exception e)
        {
            // why does this even throw
            _logger.LogWarning(e, "Error during Reload.SendMessage IPC");
        }

        _logger.LogInformation("Loaded {Count} quests in total", batch.Quests.Count);
    }

    [Conditional("RELEASE")]
    private void LoadQuestsFromAssembly(ReloadBatch batch)
    {
        batch.PendingLogs.Add(new PendingLog(LogLevel.Information, "Loading quests from assembly", [], null, null));

        int skippedQuestCount = 0;
        foreach((ElementId questId, QuestRoot questRoot) in AssemblyQuestLoader.GetQuests())
        {
            try
            {
                IQuestInfo questInfo = _questData.GetQuestInfo(questId);
                Quest quest = new()
                {
                    Id = questId,
                    Root = questRoot,
                    Info = questInfo,
                    Source = Quest.ESource.Assembly
                };
                batch.Quests[quest.Id] = quest;
            }
            catch(Exception e)
            {
                ++skippedQuestCount;
                batch.PendingLogs.Add(new PendingLog(LogLevel.Debug,
                    "Not loading unknown quest {QuestId} from assembly: {Message}", [questId, e.Message], null, null));
            }
        }

        batch.SkippedQuestCount = skippedQuestCount;

        // 落後於國際服的地區(例如台服)其 Quest 表只涵蓋已實裝的內容:更新的任務要嘛整列不存在,
        // 要嘛列在但欄位全空(Name 為空、IssuerLocation 為 0)。QuestData 建表時就以 IssuerLocation > 0
        // 過濾,所以這些任務不會進到 _quests,也就不會出現在任務日誌、任務搜尋、優先任務等任何清單裡。
        // 這裡不重複那道過濾,只是把「被略過幾條」寫成使用者看得到的診斷 —— 原本只有 Debug 級的
        // 逐條訊息,在動輒數十萬行的實機 log 裡等於靜默,害得同一個問題要從頭查一次。
        if (skippedQuestCount > 0)
        {
            batch.PendingLogs.Add(new PendingLog(LogLevel.Information,
                "Skipped {Count} quest paths whose quests are missing from this client's Quest sheet (content not released in this region yet)",
                [skippedQuestCount], null, null));
        }

        batch.PendingLogs.Add(new PendingLog(LogLevel.Information, "Loaded {Count} quests from assembly",
            [batch.Quests.Count], null, null));
    }

    [Conditional("DEBUG")]
    private void LoadQuestsFromProjectDirectory(ReloadBatch batch)
    {
        DirectoryInfo? solutionDirectory = _pluginInterface.AssemblyLocation.Directory?.Parent?.Parent;
        if (solutionDirectory != null)
        {
            DirectoryInfo pathProjectDirectory = new(Path.Combine(solutionDirectory.FullName, "QuestPaths"));
            if (pathProjectDirectory.Exists)
            {
                try
                {
                    foreach(string expansionFolder in ExpansionData.ExpansionFolders.Values)
                    {
                        LoadFromDirectory(batch,
                            new(Path.Combine(pathProjectDirectory.FullName, expansionFolder)),
                            Quest.ESource.ProjectDirectory,
                            LogLevel.Trace);
                    }
                }
                catch(Exception e)
                {
                    batch.Quests.Clear();

                    batch.PendingLogs.Add(new PendingLog(LogLevel.Error, string.Empty, [], null,
                        $"Unable to load quests - {e.GetType().Name}: {e.Message}"));
                    batch.PendingLogs.Add(new PendingLog(LogLevel.Error,
                        "Failed to load quests from project directory", [], e, null));
                }
            }
        }
    }

    private void LoadCfcIds(ReloadBatch batch)
    {
        foreach(Quest quest in batch.Quests.Values)
        {
            foreach(QuestSequence dutySequence in quest.AllSequences())
            {
                foreach(QuestStep dutyStep in dutySequence.Steps.Where(x =>
                    x.InteractionType is EInteractionType.Duty or EInteractionType.SinglePlayerDuty))
                {
                    if (dutyStep is { InteractionType: EInteractionType.Duty, DutyOptions: { } dutyOptions })
                    {
                        batch.ContentFinderConditionIds[dutyOptions.ContentFinderConditionId] = (quest.Id, dutyStep);
                        if (dutyOptions.LowPriority)
                        {
                            batch.LowPriorityContentFinderConditionQuests.Add((dutyOptions.ContentFinderConditionId,
                                quest.Id, dutySequence.Sequence));
                        }
                    }
                    else if (dutyStep.InteractionType == EInteractionType.SinglePlayerDuty &&
                             _territoryData.TryGetContentFinderConditionForSoloInstance(quest.Id,
                                 dutyStep.SinglePlayerDutyIndex, out TerritoryData.ContentFinderConditionData? cfcData))
                    {
                        batch.ContentFinderConditionIds[cfcData.ContentFinderConditionId] = (quest.Id, dutyStep);
                    }
                }
            }
        }
    }

    /// <remarks>
    /// 🔴 驗證本身是<b>背景工作</b>，<see cref="_prepareGate"/> 只序列化「排出去」這個動作
    /// ⇒ 一定要把批次編號一起帶過去，否則兩批同時在跑時是「誰後寫完誰贏」。
    /// </remarks>
    private void ValidateQuests(ReloadBatch batch)
    {
        _questValidator.Validate(batch.Quests.Values.Where(x => x.Source != Quest.ESource.Assembly).ToList(),
            batch.Sequence);
    }

    private void LoadQuestFromStream(ReloadBatch batch, string fileName, Stream stream, Quest.ESource source)
    {
        if (source == Quest.ESource.UserDirectory)
        {
            batch.PendingLogs.Add(new PendingLog(LogLevel.Trace, "Loading quest from '{FileName}'", [fileName], null,
                null));
        }
        ElementId? questId = ExtractQuestIdFromName(fileName);
        if (questId == null)
        {
            return;
        }

        JsonNode questNode = JsonNode.Parse(stream)!;
        _jsonSchemaValidator.Enqueue(questId, questNode);

        QuestRoot questRoot = questNode.Deserialize<QuestRoot>()!;
        IQuestInfo questInfo = _questData.GetQuestInfo(questId);
        Quest quest = new()
        {
            Id = questId,
            Root = questRoot,
            Info = questInfo,
            Source = source
        };
        batch.Quests[quest.Id] = quest;
    }

    private void LoadFromDirectory(ReloadBatch batch, DirectoryInfo directory, Quest.ESource source,
        LogLevel logLevel = LogLevel.Information)
    {
        if (!directory.Exists)
        {
            batch.PendingLogs.Add(new PendingLog(LogLevel.Information,
                "Not loading quests from {DirectoryName} (doesn't exist)", [directory], null, null));
            return;
        }

        if (source == Quest.ESource.UserDirectory)
        {
            batch.PendingLogs.Add(new PendingLog(logLevel, "Loading quests from {DirectoryName}", [directory], null,
                null));
        }
        foreach(FileInfo fileInfo in directory.GetFiles("*.json"))
        {
            try
            {
                using FileStream stream = new(fileInfo.FullName, FileMode.Open, FileAccess.Read);
                LoadQuestFromStream(batch, fileInfo.Name, stream, source);
            }
            catch(Exception e)
            {
                throw new InvalidDataException($"Unable to load file {fileInfo.FullName}", e);
            }
        }

        foreach(DirectoryInfo childDirectory in directory.GetDirectories())
        {
            LoadFromDirectory(batch, childDirectory, source, logLevel);
        }
    }

    private static ElementId? ExtractQuestIdFromName(string resourceName)
    {
        string name = resourceName.Substring(0, resourceName.Length - ".json".Length);
        name = name.Substring(name.LastIndexOf('.') + 1);

        if (!name.Contains('_', StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = name.Split('_', 2);
        return ElementId.FromString(parts[0]);
    }

    public bool IsKnownQuest(ElementId questId)
    {
        return _quests.ContainsKey(questId);
    }

    public bool TryGetQuest(ElementId questId, [NotNullWhen(true)] out Quest? quest)
    {
        return _quests.TryGetValue(questId, out quest);
    }

    public List<QuestInfo> GetKnownClassJobQuests(Job classJob, bool includeRoleQuests = true)
    {
        List<QuestInfo> allQuests = [.. _questData.GetClassJobQuests(classJob, includeRoleQuests)];
        if (classJob.AsJob() != classJob)
        {
            allQuests.AddRange(_questData.GetClassJobQuests(classJob.AsJob(), includeRoleQuests));
        }

        return allQuests
            .Where(x => IsKnownQuest(x.QuestId))
            .ToList();
    }

    public bool TryGetDutyByContentFinderConditionId(uint cfcId, [NotNullWhen(true)] out DutyOptions? dutyOptions)
    {
        if (_contentFinderConditionIds.TryGetValue(cfcId, out (ElementId QuestId, QuestStep Step) value))
        {
            dutyOptions = value.Step.DutyOptions;
            return dutyOptions != null;
        }

        dutyOptions = null;
        return false;
    }

#if DEBUG
    internal FileInfo AssemblyLocation => _pluginInterface.AssemblyLocation;
    public static string GetFilename(IQuestInfo info)
    {
        return $"{info.QuestId}_{info.SimplifiedName}.json";
    }
    public (bool, string) OpenEditor(IQuestInfo info)
    {
        _logger.LogDebug("OpenEditor IQuestInfo");
        return OpenEditor(AssemblyLocation, GetFilename(info));
    }
    public (bool, string) OpenEditor(ushort questId)
    {
        _logger.LogDebug("OpenEditor ushort");
        if (TryGetQuest(new QuestId(questId), out Quest? quest))
        {
            return OpenEditor(AssemblyLocation, GetFilename(quest.Info));
        }
        return (false, $"could not get quest from {questId}");
    }
    public unsafe (bool, string) OpenEditor()
    {
        _logger.LogDebug("OpenEditor trackedQuests");
        QuestManager* questManager = QuestManager.Instance();
        ushort? questId = null;
        if (questManager != null)
        {
            for(int i = questManager->TrackedQuests.Length - 1; i >= 0; --i)
            {
                TrackingWork trackedQuest = questManager->TrackedQuests[i];
                switch (trackedQuest.QuestType)
                {
                    case 1:
                        questId = questManager->NormalQuests[trackedQuest.Index].QuestId;
                        break;
                    case 2:
                        break;
                }
                if (questId != null)
                {
                    break;
                }
            }
        }
        if (questId != null)
        {
            return OpenEditor(questId.Value);
        }
        return (false, "could not get tracked quest");
    }

    public static (bool, string) OpenEditor(FileInfo assemblyLocation, string filename)
    {
        DirectoryInfo? targetFolder = new(Path.Combine(assemblyLocation.Directory!.Parent!.Parent!.FullName, "QuestPaths"));
        if (targetFolder == null)
        {
            return (false, "couldn't find QuestPaths folder");
        }
        FileInfo? file = FindFilenameInDirectory(targetFolder, filename);
        if (file == null)
        {
            return (false, $"couldn't find {filename}");
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = filename,
            WorkingDirectory = file.DirectoryName,
            UseShellExecute = true
        });
        return (true, file.FullName);
    }

    public static FileInfo? FindFilenameInDirectory(DirectoryInfo root, string filename)
    {
        foreach(FileInfo file in root.GetFiles())
        {
            if (file.Name == filename)
            {
                return file;
            }
        }
        foreach(DirectoryInfo directory in root.GetDirectories())
        {
            if (FindFilenameInDirectory(directory, filename) is FileInfo result)
            {
                return result;
            }
        }
        return null;
    }
#endif
}
