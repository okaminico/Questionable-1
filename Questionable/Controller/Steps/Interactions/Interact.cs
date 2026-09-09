using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Shared;
using Questionable.Controller.Utils;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace Questionable.Controller.Steps.Interactions;

internal static class Interact
{
    internal sealed class Factory(AutomatonIpc automatonIpc, Configuration configuration) : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType is EInteractionType.AcceptQuest or EInteractionType.CompleteQuest
                or EInteractionType.SinglePlayerDuty)
            {
                // 'PreventQuestCompletion' config check
                if (step.InteractionType is EInteractionType.CompleteQuest && configuration.Advanced.PreventQuestCompletion)
                {
                    yield break;
                }

                if (step.Emote != null)
                {
                    yield break;
                }

                if (step.ChatMessage != null)
                {
                    yield break;
                }

                if (step.ItemId != null)
                {
                    yield break;
                }

                if (step.DataId == null)
                {
                    yield break;
                }
            }
            else if (step.InteractionType == EInteractionType.PurchaseItem)
            {
                if (step.DataId == null)
                {
                    yield break;
                }
            }
            else if (step.InteractionType == EInteractionType.Snipe)
            {
                if (!automatonIpc.IsAutoSnipeEnabled)
                {
                    yield break;
                }
            }
            else if (step.InteractionType == EInteractionType.UnlockTaxiStand)
            {
                if (step.TaxiStandId == null)
                {
                    yield break;
                }
            }
            else if (step.InteractionType != EInteractionType.Interact)
            {
                yield break;
            }

            ArgumentNullException.ThrowIfNull(step.DataId);

            // if we're fast enough, it is possible to get the smalltalk prompt
            if (sequence.Sequence == 0 && sequence.Steps.IndexOf(step) == 0)
            {
                yield return new WaitAtEnd.WaitDelay();
            }

            yield return new Task(
                step.DataId.Value,
                quest,
                step.InteractionType,
                step.TargetTerritoryId != null || quest.Id is SatisfactionSupplyNpcId ||
                step.SkipConditions is { StepIf.Never: true } || step.InteractionType == EInteractionType.PurchaseItem || step.DataId == 1052475,
                step.PickUpItemId,
                step.TaxiStandId,
                step.SkipConditions?.StepIf,
                step.CompletionQuestVariablesFlags);
        }
    }

    internal sealed record Task
    (
        uint DataId,
        Quest? Quest,
        EInteractionType InteractionType,
        bool SkipMarkerCheck = false,
        uint? PickUpItemId = null,
        uint? TaxiStandId = null,
        SkipStepConditions? SkipConditions = null,
        List<QuestWorkValue?>? CompletionQuestVariablesFlags = null) : ITask
    {
        public List<QuestWorkValue?> CompletionQuestVariablesFlags { get; } = CompletionQuestVariablesFlags ?? [];

        public bool HasCompletionQuestVariablesFlags { get; } =
            Quest != null &&
            CompletionQuestVariablesFlags != null &&
            QuestWorkUtils.HasCompletionFlags(CompletionQuestVariablesFlags);

        public bool ShouldRedoOnInterrupt()
        {
            return true;
        }

        public override string ToString()
        {
            return $"Interact{(HasCompletionQuestVariablesFlags ? "*" : "")}({DataId})";
        }
    }

    internal sealed class DoInteract
    (
        GameFunctions gameFunctions,
        QuestFunctions questFunctions,
        CameraFunctions cameraFunctions,
        Configuration configuration,
        ICondition condition,
        IChatGui chatGui,
        IObjectTable objectTable,
        ILogger<DoInteract> logger)
        : TaskExecutor<Task>, IConditionChangeAware
    {
        private DateTime _continueAt = DateTime.MinValue;
        private EInteractionState _interactionState = EInteractionState.None;
        private bool _needsFacing;
        private bool _needsUnmount;
        private bool _reportedGameObjNull;

        /// <summary>
        ///     A slight delay when we think an interaction has ended, to make sure that we're processing "Action cancelled"
        ///     prior to the next step (in case we're attacked).
        /// </summary>
        private bool delayedFinalCheck;

        public Quest? Quest => Task.Quest;
        public EInteractionType InteractionType { get; set; }

        // 🔴 MiniTaskController 每一幀都會先呼叫這個(在 Update() 之前),判斷「這個任務是不是被打斷了」。
        // 基底實作(TaskExecutor<T>.WasInterrupted)借用的是 ProgressContext 那套本來設計給「真的在
        // 使用招式卻被擊退/擊倒打斷」的讀條欄位(ActionManager 的 CastTimeElapsed/CastTimeTotal)。
        // 套在單純的物件互動上本來就勉強,遇到這種互動本身會觸發進入單人任務副本
        // (OccupiedInEvent 短暫 True→False)的情況更容易被那套讀條邏輯誤判成「被打斷」——
        // 而這時 _interactionState 早已經在 OnConditionChange 裡被標成 InteractionConfirmed,
        // 下一輪 Update() 原本就會正常判定完成。誤判搶在那之前打斷整個任務佇列,QuestController
        // 的復原邏輯又會把這一步的 MoveTo+Interact 整套重排回去,導致在同一個互動物件前
        // 不斷重複「走過去、互動、被誤判打斷、重排、再走過去」——卡在互動物件前原地跑步。
        // ⇒ 已經透過 OccupiedInEvent 旗標確認互動成功時,不要再讓這套借來的讀條偵測蓋過去。
        public override bool WasInterrupted()
        {
            if (_interactionState == EInteractionState.InteractionConfirmed)
                return false;

            return base.WasInterrupted();
        }

        public override ETaskResult Update()
        {
            logger.LogDebug($"Entered Update, _continueAt: {_continueAt}");
            if (DateTime.Now <= _continueAt)
            {
                return ETaskResult.StillRunning;
            }

            if (_needsUnmount)
            {
                if (condition[ConditionFlag.Mounted])
                {
                    logger.LogDebug("Attempting unmount");
                    gameFunctions.Unmount();
                    _continueAt = DateTime.Now.AddSeconds(1);
                    return ETaskResult.StillRunning;
                }
                else
                {
                    _needsUnmount = false;
                }
            }
            else
            {
                logger.LogDebug("Does not need unmount");
            }

            if (Task.PickUpItemId is { } pickUpItemId)
            {
                logger.LogDebug($"PickUpItemId {pickUpItemId}");
                unsafe
                {
                    InventoryManager* inventoryManager = InventoryManager.Instance();
                    if (inventoryManager->GetInventoryItemCount(pickUpItemId) > 0)
                    {
                        return ETaskResult.TaskComplete;
                    }
                }
            }
            else if (Task.TaxiStandId is { } taxiStandId)
            {
                logger.LogDebug($"TaxiStandId {taxiStandId}");
                unsafe
                {
                    UIState* uiState = UIState.Instance();
                    if (uiState->IsChocoboTaxiStandUnlocked(taxiStandId))
                    {
                        return ETaskResult.TaskComplete;
                    }
                }
            }
            else if (InteractionType == EInteractionType.Gather && condition[ConditionFlag.Gathering])
            {
                return ETaskResult.TaskComplete;
            }
            else if (Quest != null && Task.HasCompletionQuestVariablesFlags)
            {
                logger.LogDebug("Checking QW");
                QuestProgressInfo? questWork = questFunctions.GetQuestProgressInfo(Quest.Id);

                if (questWork != null && QuestWorkUtils.MatchesQuestWork(Task.CompletionQuestVariablesFlags, questWork))
                {
                    return ETaskResult.TaskComplete;
                }
            }
            else if (ProgressContext != null)
            {
                logger.LogDebug("Entered ProgressContext");
                // 🔴 原本是先問 ProgressContext.WasInterrupted()，true 就直接 return，
                // 下面 _interactionState == InteractionConfirmed 那個分支永遠排不到——
                // 而這個互動本身會觸發進入單人任務副本，那段讀條欄位借來的判斷卡死在
                // "true" 的話，_continueAt 永遠不會被推進，Update() 每一幀都從頭跑一遍，
                // 卻永遠到不了真正的完成判定，變成無限迴圈(跟 WasInterrupted() override
                // 修的是同一個根因，這裡是另一條沒被那個修正擋到的路徑)。
                // ⇒ 已經透過 OccupiedInEvent 旗標確認成功時，優先判完成，不要讓那套
                // 讀條偵測卡住往下走。
                if (_interactionState == EInteractionState.InteractionConfirmed ||
                    ProgressContext.WasSuccessful())
                {
                    if (delayedFinalCheck)
                    {
                        return ETaskResult.TaskComplete;
                    }

                    _continueAt = DateTime.Now.AddSeconds(0.2);
                    delayedFinalCheck = true;
                    return ETaskResult.StillRunning;
                }
                else if (ProgressContext.WasInterrupted())
                {
                    return ETaskResult.StillRunning;
                }
            }
            else
            {
                logger.LogDebug("Conditions block passed");
            }

            IGameObject? gameObject = gameFunctions.FindObjectByDataId(Task.DataId);
            //if (gameObject == null || !gameObject.IsTargetable || !HasAnyMarker(gameObject))
            if (gameObject == null)
            {
                if (!_reportedGameObjNull)
                {
                    logger.LogDebug("gameObject is null");
                    _reportedGameObjNull = true;
                }
                return ETaskResult.StillRunning;
            }
            _reportedGameObjNull = false;
            logger.LogDebug("gameObject != null");

            if (_needsFacing)
            {
                logger.LogInformation("Facing target");
                cameraFunctions.Face(gameObject.Position);
                _continueAt = DateTime.Now.AddSeconds(0.2);
                _needsFacing = false;
                return ETaskResult.StillRunning;
            }
            else
            {
                logger.LogDebug("Does not need facing");
            }

            if (objectTable[0] is IPlayerCharacter player && Task.Quest != null && InteractionType == EInteractionType.AcceptQuest)
            {
                List<Job> acceptableJobs = [.. Task.Quest.Info.ClassJobs];
                Job playerJob = (Job)player.ClassJob.Value.RowId;
                if (acceptableJobs.Count >= 1 && !acceptableJobs.Contains(playerJob))
                {
                    if (!acceptableJobs[0].IsCrafter() && !acceptableJobs[0].IsGatherer())
                    {
                        acceptableJobs = [.. acceptableJobs.Prepend(configuration.General.CombatJob)];
                    }
                    else if (acceptableJobs[0].IsCrafter())
                    {
                        acceptableJobs = [.. acceptableJobs.Prepend(configuration.General.CraftingJob)];
                    }
                    else if (acceptableJobs[0].IsGatherer())
                    {
                        acceptableJobs = [.. acceptableJobs.Prepend(configuration.General.GatheringJob)];
                    }
                    if (Task.Quest.Info.AlliedSociety.Equals(EAlliedSociety.Namazu))
                    {
                        if (configuration.Advanced.NamazuPreferCraft && !acceptableJobs[0].IsCrafter())
                        {
                            acceptableJobs = [.. acceptableJobs.Prepend(configuration.General.CraftingJob)];
                        }
                        else if (!configuration.Advanced.NamazuPreferCraft && !acceptableJobs[0].IsGatherer())
                        {
                            acceptableJobs = [.. acceptableJobs.Prepend(configuration.General.GatheringJob)];
                        }
                    }
                    logger.LogInformation($"Current ClassJob {playerJob} not valid for {Task.Quest.Id}, attempting to switch");
                    unsafe
                    {
                        bool changed = false;
                        RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
                        if (gearsetModule != null)
                        {
                            for(int i = 0; i < 100; ++i)
                            {
                                RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(i);
                                if (acceptableJobs[0].Equals((Job)gearset->ClassJob))
                                {
                                    gearsetModule->EquipGearset(gearset->Id);
                                    changed = true;
                                }
                            }
                        }
                        if (!changed)
                        {
                            chatGui.PrintError($"Quest {Task.Quest.Info.Name} requires a job like {acceptableJobs[0]}, " +
                                               "but you do not have a valid job configured in QST Settings.");
                        }
                    }
                    _continueAt = DateTime.Now.AddSeconds(0.2);
                    return ETaskResult.StillRunning;
                }
            }
            else
            {
                logger.LogDebug("is not AcceptQuest");
            }

            if (!gameObject.IsTargetable || !HasAnyMarker(gameObject))
            {
                return ETaskResult.StillRunning;
            }

            TriggerInteraction(gameObject);
            return ETaskResult.StillRunning;
        }

        public void OnConditionChange(ConditionFlag flag, bool value)
        {
            if (ProgressContext != null && (ProgressContext.WasInterrupted() || ProgressContext.WasSuccessful()))
            {
                return;
            }

            logger.LogDebug("Condition change: {Flag} = {Value}", flag, value);
            if (_interactionState == EInteractionState.InteractionTriggered &&
                flag is ConditionFlag.OccupiedInQuestEvent or ConditionFlag.OccupiedInEvent &&
                value)
            {
                logger.LogInformation("Interaction was most likely triggered");
                _interactionState = EInteractionState.InteractionConfirmed;
            }
        }

        public override bool ShouldInterruptOnDamage()
        {
            return true;
        }

        protected override bool Start()
        {
            InteractionType = Task.InteractionType;

            _needsFacing = true;
            IGameObject? gameObject = gameFunctions.FindObjectByDataId(Task.DataId);
            if (gameObject == null)
            {
                logger.LogWarning("No game object with dataId {DataId}", Task.DataId);
                return false;
            }

            if (!gameObject.IsTargetable && Task.SkipConditions is { Never: false, NotTargetable: true })
            {
                logger.LogInformation("Not interacting with {DataId} because it is not targetable (but skippable)",
                    Task.DataId);
                return false;
            }

            // interacting always fails while airborne on a flying mount, regardless of the quest
            // step's own Fly/Land configuration; land (and if needed, dismount) before trying
            if (condition[ConditionFlag.InFlight])
            {
                logger.LogInformation("Preparing interaction for {DataId} by landing", Task.DataId);
                _needsUnmount = true;
                gameFunctions.Unmount();
                _continueAt = DateTime.Now.AddSeconds(1);
                return true;
            }

            // this is only relevant for followers on quests
            if (!gameObject.IsTargetable && condition[ConditionFlag.Mounted] &&
                gameObject.ObjectKind != ObjectKind.GatheringPoint)
            {
                logger.LogInformation("Preparing interaction for {DataId} by unmounting", Task.DataId);
                _needsUnmount = true;
                gameFunctions.Unmount();
                _continueAt = DateTime.Now.AddSeconds(1);
                return true;
            }

            return true;
        }

        private void TriggerInteraction(IGameObject gameObject)
        {
            ProgressContext =
                InteractionProgressContext.FromActionUseOrDefault(() =>
                {
                    if (gameFunctions.InteractWith(gameObject))
                    {
                        _interactionState = EInteractionState.InteractionTriggered;
                    }
                    else
                    {
                        _interactionState = EInteractionState.None;
                    }
                    return _interactionState != EInteractionState.None;
                });
            _continueAt = DateTime.Now.AddSeconds(0.5);
        }

        private unsafe bool HasAnyMarker(IGameObject gameObject)
        {
            if (Task.SkipMarkerCheck || gameObject.ObjectKind != ObjectKind.EventNpc)
            {
                return true;
            }

            GameObject* gameObjectStruct = (GameObject*)gameObject.Address;
            return gameObjectStruct->NamePlateIconId != 0;
        }

        private enum EInteractionState
        {
            None,
            InteractionTriggered,
            InteractionConfirmed
        }
    }
}
