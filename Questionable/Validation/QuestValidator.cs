using Microsoft.Extensions.Logging;
using Questionable.Model;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Questionable.Validation;

internal sealed class QuestValidator
{
    private readonly ILogger<QuestValidator> _logger;
    private readonly IReadOnlyList<IQuestValidator> _validators;

    /// <summary>
    /// 保護 <see cref="_validationIssues"/> 與 <see cref="_currentSequence"/>。
    /// 🔴 鎖裡只有參照指派與一次整數比較 —— 不寫記錄、不做 I/O、不打 IPC。
    /// </summary>
    private readonly object _issuesGate = new();

    /// <summary>同一時間只讓一個驗證工作真的在跑。</summary>
    /// <remarks>
    /// 🔴 <see cref="_validators"/> 是共用的單例、<see cref="IQuestValidator.Reset"/> 會改它們的狀態
    /// ⇒ 兩個驗證工作同時跑會互相踩。這把鎖<b>只有背景工作會取</b>，
    /// framework／繪製執行緒永遠不會停下來等它。
    /// </remarks>
    private readonly object _runGate = new();

    /// <summary>目前有效的批次編號，沿用 <c>QuestRegistry.ReloadBatch.Sequence</c>。</summary>
    /// <remarks>
    /// 🔴 存在的理由：<see cref="Validate"/> 把工作丟到背景執行緒，而 <c>QuestRegistry.PrepareReload</c>
    /// 的閘門只序列化「排出去」這個動作、<b>不序列化執行</b> ⇒ 連按兩次重新載入時兩個驗證工作會同時跑，
    /// 先排的那個若後寫回就會蓋掉新的結果。失敗形式是<b>驗證清單停在上一份任務路線</b>——
    /// 不報錯、也沒有任何訊息，看起來只像資料怪怪的。
    /// </remarks>
    private int _currentSequence;

    private List<ValidationIssue> _validationIssues = [];

    public QuestValidator(IEnumerable<IQuestValidator> validators, ILogger<QuestValidator> logger)
    {
        _validators = validators.ToList();
        _logger = logger;

        _logger.LogInformation("Validators: {Validators}",
            string.Join(", ", _validators.Select(x => x.GetType().Name)));
    }

    /// <remarks>
    /// 📌 <see cref="_validationIssues"/> <b>只會被整份換掉，永遠不會就地改</b>：舊版在背景工作裡
    /// 對它呼叫 <c>Clear()</c>，而 UI 每幀在繪製執行緒上迭代同一個清單 ⇒ 迭代到一半被清空
    /// 就是 <c>InvalidOperationException</c>。改成換參照之後，讀到的一律是某一份完整的快照。
    /// </remarks>
    public IReadOnlyList<ValidationIssue> Issues => _validationIssues;
    public int IssueCount => _validationIssues.Count;
    public int ErrorCount => _validationIssues.Count(x => x.Severity == EIssueSeverity.Error);

    /// <summary>清掉上一輪的結果，並宣告「從現在起有效的是第 <paramref name="sequence"/> 批」。</summary>
    /// <param name="sequence">這一批重新載入的編號（<c>QuestRegistry.ReloadBatch.Sequence</c>）。</param>
    /// <remarks>
    /// 🔑 編號在這裡就設定好（而不是等到 <see cref="Validate"/>）：解析那一段可能跑上好幾秒，
    /// 早一點設定，上一批還在跑的驗證工作就能早一點看到自己已經過期而提早收工。
    /// </remarks>
    public void Reset(int sequence)
    {
        lock(_issuesGate)
        {
            _currentSequence = sequence;
            _validationIssues = [];
        }

        foreach(IQuestValidator validator in _validators)
        {
            validator.Reset();
        }
    }

    /// <summary>排一個背景工作去驗證這一批任務路線。</summary>
    /// <param name="quests">要驗的任務；呼叫端已經 <c>ToList()</c> 過，只有這個工作看得到。</param>
    /// <param name="sequence">這一批的編號，見 <see cref="_currentSequence"/>。</param>
    /// <remarks>
    /// 連按兩次重新載入時的時序：
    /// <list type="number">
    /// <item>#1 取 <c>_prepareGate</c> → <see cref="Reset"/>(1) → <see cref="Validate"/>(…, 1) 排出去 → 放掉閘門。</item>
    /// <item>#2 取 <c>_prepareGate</c> → <see cref="Reset"/>(2)（編號變成 2）→ <see cref="Validate"/>(…, 2) → 放掉閘門。</item>
    /// <item>#1 的工作在每一條任務之間看一次編號，發現不是 1 就<b>當場收工，不寫回</b>。</item>
    /// <item>#2 的工作跑完，編號仍是 2 ⇒ 寫回。</item>
    /// </list>
    /// 舊版沒有第 3、4 步：兩個工作都會寫回，誰後寫誰贏，而先排的那個常常後跑完。
    /// </remarks>
    public void Validate(IEnumerable<Quest> quests, int sequence)
    {
        Task.Factory.StartNew(() =>
        {
            // 🔴 共用的 validators 一次只給一個工作用。等在這裡的一定是背景執行緒，
            //    framework／繪製執行緒不會被這把鎖擋住。
            lock(_runGate)
            try
            {
                if (Volatile.Read(ref _currentSequence) != sequence)
                {
                    _logger.LogInformation(
                        "Discarding quest validation #{Sequence} before it started, a newer reload has already been requested",
                        sequence);
                    return;
                }

                List<ValidationIssue> issues = [];
                Dictionary<EAlliedSociety, int> disabledTribeQuests = [];
                foreach(Quest quest in quests)
                {
                    // 每一條任務之間看一次編號：更新的一批已經排出去的話就不必再算下去了。
                    if (Volatile.Read(ref _currentSequence) != sequence)
                    {
                        _logger.LogInformation(
                            "Abandoning quest validation #{Sequence}, a newer reload has already been requested",
                            sequence);
                        return;
                    }

                    foreach(IQuestValidator validator in _validators)
                    {
                        try
                        {
                            foreach(ValidationIssue issue in validator.Validate(quest))
                            {
                                /*
                                var level = issue.Severity == EIssueSeverity.Error
                                    ? LogLevel.Warning
                                    : LogLevel.Debug;
                                _logger.Log(level,
                                    "Validation failed: {QuestId} ({QuestName}) / {QuestSequence} / {QuestStep} - {Description}",
                                    issue.ElementId, quest.Info.Name, issue.Sequence, issue.Step, issue.Description);
                                */
                                if (issue.Type == EIssueType.QuestDisabled && quest.Info.AlliedSociety != EAlliedSociety.None)
                                {
                                    disabledTribeQuests.TryAdd(quest.Info.AlliedSociety, 0);
                                    disabledTribeQuests[quest.Info.AlliedSociety]++;
                                }
                                else
                                {
                                    issues.Add(issue);
                                }
                            }
                        }
                        catch(ArgumentException e)
                        {
                            _logger.LogError(e, $"Unable to validate {quest.Info.QuestId} {quest.Info.Name}");
                        }
                    }
                }

                List<ElementId?> disabledQuests = issues
                    .Where(x => x.Type == EIssueType.QuestDisabled)
                    .Select(x => x.ElementId)
                    .ToList();

                List<ValidationIssue> result = issues
                    .Where(x => !disabledQuests.Contains(x.ElementId) || x.Type == EIssueType.QuestDisabled)
                    .OrderBy(x => x.ElementId)
                    .ThenBy(x => x.Sequence)
                    .ThenBy(x => x.Step)
                    .ThenBy(x => x.Description)
                    .Concat(DisabledTribesAsIssues(disabledTribeQuests))
                    .ToList();

                lock(_issuesGate)
                {
                    if (_currentSequence != sequence)
                    {
                        _logger.LogInformation(
                            "Discarding quest validation #{Sequence}, a newer reload has already been published",
                            sequence);
                        return;
                    }

                    _validationIssues = result;
                }
            }
            catch(Exception e)
            {
                _logger.LogError(e, "Unable to validate quests");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public List<ValidationIssue> GetIssues(ElementId elementId)
    {
        // 讀一次參照再用，不要在同一個運算式裡讀兩次欄位（中途可能被換成新的一份）。
        List<ValidationIssue> snapshot = _validationIssues;
        return snapshot.Where(x => x.ElementId == elementId).ToList();
    }

    private static IEnumerable<ValidationIssue> DisabledTribesAsIssues(Dictionary<EAlliedSociety, int> disabledTribeQuests)
    {
        return disabledTribeQuests
            .OrderBy(x => x.Key)
            .Select(x => new ValidationIssue
            {
                ElementId = null,
                Sequence = null,
                Step = null,
                AlliedSociety = x.Key,
                Type = EIssueType.QuestDisabled,
                Severity = EIssueSeverity.None,
                Description = $"{x.Value} disabled quest(s)"
            });
    }
}
