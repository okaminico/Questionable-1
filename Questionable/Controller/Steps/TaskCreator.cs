using Dalamud.Plugin.Services;
using ECommons.MathHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Interactions;
using Questionable.Controller.Steps.Shared;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
namespace Questionable.Controller.Steps;

internal sealed class TaskCreator
(
    IServiceProvider serviceProvider,
    TerritoryData territoryData,
    IClientState clientState,
    IChatGui chatGui,
    IFramework framework,
    ILogger<TaskCreator> logger)
{
    private readonly IChatGui _chatGui = chatGui;
    private readonly IClientState _clientState = clientState;

    /// <summary>只用來把聊天輸出釘回 framework 執行緒，見 <see cref="ChatGuiExtensions"/>。</summary>
    private readonly IFramework _framework = framework;
    private readonly ILogger<TaskCreator> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly TerritoryData _territoryData = territoryData;

    /// <summary>把一個任務步驟展開成要跑的工作清單。</summary>
    /// <param name="defer">
    /// 不是 <see langword="null"/> 時，這裡面的<b>聊天輸出與記錄</b>交給它安排；
    /// <b>回傳值語意完全不變</b>——工作清單一樣是同步算好、同步回去，呼叫端照樣可以在鎖裡
    /// 直接 <c>foreach</c> 進佇列。
    /// </param>
    /// <remarks>
    /// 🔴 <c>QuestController.ExecuteNextStep</c> 是持著 <c>_progressLock</c> 呼叫這一支的，
    /// 而這裡有 4 個聊天輸出與 5 行記錄：<c>ILogger</c> 最後落到 Dalamud 的 Serilog sink
    /// （那邊自己有鎖、還會做檔案 I/O），聊天則是進到本 pin 那個沒有同步的 <c>Queue</c>。
    /// <para>
    /// 🔴 <b>不能改成「在鎖外先算好」</b>：算工作清單要讀 <c>CurrentQuest</c> 的序列與步數，
    /// 搬到鎖外就是縮小引擎狀態變更的原子性。所以拆的是輸出，不是計算。
    /// </para>
    /// <para>
    /// 📌 所有訊息的參數都在<b>加進延後清單之前</b>就求好值（尤其 <c>newTasks.Count</c>：
    /// 下面的 <c>RemoveRange</c> 會改變它），所以延後寫出去的字與「當場寫」逐字相同。
    /// </para>
    /// <para>
    /// 📌 <paramref name="defer"/> 不給的時候（<c>Gather.cs</c> 那個在任務執行器裡的呼叫端）
    /// 行為逐字不變：就地依序執行。
    /// </para>
    /// </remarks>
    public IReadOnlyList<ITask> CreateTasks(Quest quest, byte sequenceNumber, QuestSequence? sequence, QuestStep? step,
        System.Action<System.Action>? defer = null)
    {
        List<ITask> newTasks;
# if !DEBUG
        if (quest.Root.Disabled && sequenceNumber.InRange(1, 2, true))
        {
            var reason = (quest.Root.Comment ?? "<no reason specified>").Split('\n', 2)[0];
            // CreateTasks 從 IPC 端點 Questionable.StartQuest／StartSingleQuest 可達
            //（QuestionableIpc.StartQuest -> QuestController.StartSingleQuest -> ExecuteNextStep -> CreateTasks），
            // 而 IPC 跑在呼叫端外掛的執行緒上。見 ChatGuiExtensions。
            string disabledMessage =
                $"The quest '{quest.Info.Name}' has been marked as Disabled for the following reason: {reason}";
            RunOrDefer(defer, () =>
            {
                // 🔴 這三行是「連續三行、要照順序讀」的，所以放在同一個延後項目裡。
                _chatGui.PrintErrorOnFrameworkThread(_framework, disabledMessage,
                    CommandHandler.MessageTag, CommandHandler.TagColor);
                _chatGui.PrintErrorOnFrameworkThread(_framework, "We recommend you complete this quest manually, as the provided path may not run successfully.",
                    CommandHandler.MessageTag, CommandHandler.TagColor);
                _chatGui.PrintErrorOnFrameworkThread(_framework, "Thank you for your patience as we expand QST's support to include this quest in a future update.",
                    CommandHandler.MessageTag, CommandHandler.TagColor);
            });
        }
# endif
        if (sequence == null)
        {
            if (!quest.Root.Disabled)
            {
                string missingSequenceMessage =
                    $"Path for quest '{quest.Info.Name}' ({quest.Id}) does not contain sequence {sequenceNumber}, please report this: https://github.com/PunishXIV/Questionable/discussions/20";
                RunOrDefer(defer, () => _chatGui.PrintErrorOnFrameworkThread(_framework, missingSequenceMessage,
                    CommandHandler.MessageTag, CommandHandler.TagColor));
            }
            newTasks = [new WaitAtEnd.WaitNextStepOrSequence()];
        }
        else if (step == null)
        {
            newTasks = [new WaitAtEnd.WaitNextStepOrSequence()];
        }
        else
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            newTasks = scope.ServiceProvider.GetRequiredService<IEnumerable<ITaskFactory>>()
                .SelectMany(x =>
                {
                    List<ITask> tasks = x.CreateAllTasks(quest, sequence, step).ToList();

                    if (tasks.Count > 0 && _logger.IsEnabled(LogLevel.Trace))
                    {
                        string factoryName = x.GetType().FullName ?? x.GetType().Name;
                        if (factoryName.Contains('.', StringComparison.Ordinal))
                        {
                            factoryName = factoryName[(factoryName.LastIndexOf('.') + 1)..];
                        }

                        string taskNames = string.Join(", ", tasks.Select(y => y.ToString()));
                        RunOrDefer(defer, () => _logger.LogTrace("Factory {FactoryName} created Task {TaskNames}",
                            factoryName, taskNames));
                    }

                    return tasks;
                })
                .ToList();

            SinglePlayerDuty.StartSinglePlayerDuty? singlePlayerDutyTask = newTasks
                .Where(y => y is SinglePlayerDuty.StartSinglePlayerDuty)
                .Cast<SinglePlayerDuty.StartSinglePlayerDuty>()
                .FirstOrDefault();
            if (singlePlayerDutyTask != null &&
                _territoryData.TryGetContentFinderCondition(singlePlayerDutyTask.ContentFinderConditionId,
                    out TerritoryData.ContentFinderConditionData? cfcData))
            {
                // if we have a single player duty in queue, we check if we're in the matching territory
                // if yes, skip all steps before (e.g. teleporting, waiting for navmesh, moving, interacting)
                if (_clientState.TerritoryType == cfcData.TerritoryId)
                {
                    int index = newTasks.IndexOf(singlePlayerDutyTask);
                    // ⚠️ 這兩個值必須在 RemoveRange 之前抄走：延後之後才讀 newTasks.Count 會拿到刪過的數字。
                    int skippedTaskCount = index + 1;
                    int totalCount = newTasks.Count;
                    RunOrDefer(defer, () => _logger.LogWarning(
                        "Skipping {SkippedTaskCount} out of {TotalCount} tasks, questionable was started while in single player duty",
                        skippedTaskCount, totalCount));

                    newTasks.RemoveRange(0, index + 1);
                    ITask? nextTask = newTasks.FirstOrDefault();
                    int remainingTaskCount = newTasks.Count;
                    RunOrDefer(defer, () => _logger.LogInformation(
                        "Next actual task: {NextTask}, total tasks left: {RemainingTaskCount}",
                        nextTask, remainingTaskCount));
                }
            }
        }

        if (newTasks.Count == 0)
        {
            RunOrDefer(defer, () => _logger.LogInformation("Nothing to execute for step?"));
        }
        else
        {
            ElementId questId = quest.Id;
            int? stepIndex = step != null ? sequence?.Steps.IndexOf(step) : null;
            string taskList = string.Join(", ", newTasks.Select(x => x.ToString()));
            RunOrDefer(defer, () => _logger.LogInformation("Tasks for {QuestId}, {Sequence}, {Step}: {Tasks}",
                questId, sequenceNumber, stepIndex, taskList));
        }

        return newTasks;
    }

    /// <summary>持鎖的呼叫端給了 <paramref name="defer"/> 就收進它的延後清單，否則當場做。</summary>
    private static void RunOrDefer(System.Action<System.Action>? defer, System.Action action)
    {
        if (defer != null)
        {
            defer(action);
        }
        else
        {
            action();
        }
    }
}

