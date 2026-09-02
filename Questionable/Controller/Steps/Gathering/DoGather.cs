using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using Questionable.Functions;
using Questionable.Model.Gathering;
using Questionable.Model.Questing;
using Questionable.Utils;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
namespace Questionable.Controller.Steps.Gathering;

internal static class DoGather
{
    internal sealed record Task
    (
        GatheringController.GatheringRequest Request,
        GatheringNode Node,
        bool RevisitRequired) : ITask, IRevisitAware
    {
        public bool RevisitTriggered { get; private set; }

        public void OnRevisit()
        {
            RevisitTriggered = true;
        }

        public override string ToString()
        {
            return $"DoGather{(RevisitRequired ? " if revisit" : "")}";
        }
    }

    internal sealed class GatherExecutor
    (
        GatheringController gatheringController,
        GameFunctions gameFunctions,
        IGameGuiAdapter gameGui,
        ICondition condition,
        ILogger<GatherExecutor> logger) : TaskExecutor<Task>
    {
        private Queue<EAction>? _actionQueue;
        private SlotInfo? _slotToGather;
        private bool _usedLuck;
        private bool _wasGathering;

        protected override bool Start()
        {
            return true;
        }

        public override unsafe ETaskResult Update()
        {
            if (Task is { RevisitRequired: true, RevisitTriggered: false })
            {
                logger.LogInformation("No revisit");
                return ETaskResult.TaskComplete;
            }

            if (gatheringController.HasNodeDisappeared(Task.Node))
            {
                logger.LogInformation("Node disappeared");
                return ETaskResult.TaskComplete;
            }

            if (GameFunctions.GetFreeInventorySlots() == 0)
            {
                throw new TaskException("Inventory full");
            }

            if (condition[ConditionFlag.Gathering])
            {
                if (gameGui.TryGetAddonByName("GatheringMasterpiece", out AtkUnitBase* _))
                {
                    return ETaskResult.TaskComplete;
                }

                _wasGathering = true;

                if (gameGui.TryGetAddonByName("Gathering", out AddonGathering* addonGathering))
                {
                    if (gatheringController.HasRequestedItems())
                    {
                        AddonPressGuard.PressCallbackInt("Gathering", &addonGathering->AtkUnitBase, -1);
                    }
                    else
                    {
                        List<SlotInfo> slots = ReadSlots(addonGathering);
                        if (Task.Request.Collectability > 0)
                        {
                            SlotInfo slot = slots.Single(x => x.ItemId == Task.Request.ItemId);
                            logger.LogDebug($"Collectible=true, clicking {slot.Index} {slot.ItemId}");
                            AddonPressGuard.PressCallbackInt("Gathering", &addonGathering->AtkUnitBase, slot.Index, AddonPressGuard.RoutineRePressEscapeFrames);
                        }
                        else
                        {
                            // 🔴 AtkValues 是指標欄位(addon 剛 setup／正在拆解時為 null),
                            // 長度另存在 AtkValuesCount。裸讀 [109]/[110] 在 null 時是
                            // AccessViolationException(corrupted-state exception,try/catch 攔不到),
                            // 長度不足時讀的是陣列後方的堆積垃圾 —— 而這兩個值是「完整度」,
                            // 垃圾值會讓 GetNextActions 依它挑技能。讀不到就當作 0/0
                            // (與 GatheringMasterpiece 那邊「讀不到 ⇒ 不進場」同語意)。
                            NodeCondition nodeCondition = new(
                                ECommons.GenericHelpers.GetAtkValueUInt(&addonGathering->AtkUnitBase, 109),
                                ECommons.GenericHelpers.GetAtkValueUInt(&addonGathering->AtkUnitBase, 110));
                            logger.LogDebug($"NodeCondition: {nodeCondition.CurrentIntegrity}/{nodeCondition.MaxIntegrity}");

                            if (_actionQueue != null && _actionQueue.TryPeek(out EAction nextAction))
                            {
                                if (gameFunctions.UseAction(nextAction))
                                {
                                    logger.LogDebug($"Action: {nextAction}");
                                    _actionQueue.Dequeue();
                                }

                                return ETaskResult.StillRunning;
                            }

                            _actionQueue = GetNextActions(nodeCondition, slots);
                            if (_actionQueue == null)
                            {
                                logger.LogDebug("No actions returned by GetNextActions");
                                AddonPressGuard.PressCallbackInt("Gathering", &addonGathering->AtkUnitBase, -1);
                                return ETaskResult.TaskComplete;
                            }
                            else if (_actionQueue.Count == 0)
                            {
                                SlotInfo? slot = _slotToGather ?? slots.SingleOrDefault(x => x.ItemId == Task.Request.ItemId) ?? slots.MinBy(x => x.ItemId);
                                if (slot?.ItemId is >= 2 and <= 19)
                                {
                                    InventoryManager* inventoryManager = InventoryManager.Instance();
                                    if (inventoryManager->GetInventoryItemCount(slot.ItemId) == 9999)
                                    {
                                        slot = null;
                                    }
                                }

                                if (slot != null)
                                {
                                    AddonPressGuard.PressCallbackInt("Gathering", &addonGathering->AtkUnitBase, slot.Index, AddonPressGuard.RoutineRePressEscapeFrames);
                                }
                                else
                                {
                                    AddonPressGuard.PressCallbackInt("Gathering", &addonGathering->AtkUnitBase, -1);
                                }
                            }
                        }
                    }
                }
            }

            return _wasGathering && !condition[ConditionFlag.Gathering]
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        /// <summary>
        /// SearchNodeById 找不到 id 時回 null，找到的節點型別不是文字節點時
        /// GetAsAtkTextNode() 也回 null。兩層一起擋掉。
        /// </summary>
        private static unsafe AtkTextNode* AsTextNode(AtkResNode* node)
            => node == null ? null : node->GetAsAtkTextNode();

        private unsafe List<SlotInfo> ReadSlots(AddonGathering* addonGathering)
        {
            List<SlotInfo> slots = [];
            for(int i = 0; i < 8; ++i)
            {
                // +8 = new item?
                uint itemId = addonGathering->ItemIds[i];
                if (itemId == 0)
                {
                    continue;
                }

                AtkComponentCheckBox* atkCheckbox = addonGathering->GatheredItemComponentCheckbox[i].Value;

                // 🔴 這條鏈上每一跳都合法回 null：向量元素本身、SearchNodeById 找不到 id、
                // 以及節點型別不符時的 GetAsAtkTextNode()／GetAsAtkComponentNode()。
                // 原本一跳都沒判，任一跳為 null 就是往位址 0 附近讀 NodeText＝AccessViolation
                //（corrupted-state exception，try/catch 攔不到）。
                //
                // 🔴 失敗語意刻意選「保留 slot，欄位退回預設值」而不是 continue 丟掉整個 slot：
                // itemId 是從 addonGathering->ItemIds[i] 讀到的，這一格確實有東西；
                // 而 GetNextActions 對備選道具用的是 slots.Single(...)，少一個 slot 會直接擲例外。
                // 預設值就是原本「TryParse 失敗」時已經在用的那組（0／0／1），語意不變。
                // 這是每影格讀採集面板的路徑，不寫 log。
                int gatheringChance = 0;
                int boonChance = 0;
                int quantity = 1;

                if (atkCheckbox != null)
                {
                    AtkTextNode* atkGatheringChance = AsTextNode(atkCheckbox->UldManager.SearchNodeById(10));
                    if (atkGatheringChance != null &&
                        int.TryParse(atkGatheringChance->NodeText.ToString(), out int parsedGatheringChance))
                    {
                        gatheringChance = parsedGatheringChance;
                    }

                    AtkTextNode* atkBoonChance = AsTextNode(atkCheckbox->UldManager.SearchNodeById(16));
                    if (atkBoonChance != null &&
                        int.TryParse(atkBoonChance->NodeText.ToString(), out int parsedBoonChance))
                    {
                        boonChance = parsedBoonChance;
                    }

                    AtkResNode* imageResNode = atkCheckbox->UldManager.SearchNodeById(31);
                    AtkComponentNode* atkImage = imageResNode == null ? null : imageResNode->GetAsAtkComponentNode();
                    if (atkImage != null && atkImage->Component != null)
                    {
                        AtkTextNode* atkQuantity = AsTextNode(atkImage->Component->UldManager.SearchNodeById(7));
                        if (atkQuantity != null && atkQuantity->IsVisible() &&
                            int.TryParse(atkQuantity->NodeText.ToString(), out int parsedQuantity))
                        {
                            quantity = parsedQuantity;
                        }
                    }
                }

                SlotInfo slot = new(i, itemId, gatheringChance, boonChance, quantity);
                slots.Add(slot);
            }

            logger.LogDebug("Slots: {Slots}", string.Join(", ", slots));
            return slots;
        }

        [SuppressMessage("ReSharper", "UnusedParameter.Local")]
        private Queue<EAction>? GetNextActions(NodeCondition nodeCondition, List<SlotInfo> slots)
        {
            // it's possible the item has disappeared
            if (_slotToGather != null && slots.All(x => x.Index != _slotToGather.Index))
            {
                _slotToGather = null;
            }

            //uint gp = objectTable.CurrentGp;
            Queue<EAction> actions = new();

            //if (!gameFunctions.HasStatus(EStatus.GatheringRateUp))
            //{
            // do we have an alternative item? only happens for 'evaluation' leve quests
            if (Task.Request.AlternativeItemId != 0)
            {
                SlotInfo alternativeSlot = slots.Single(x => x.ItemId == Task.Request.AlternativeItemId);

                if (alternativeSlot.GatheringChance == 100)
                {
                    _slotToGather = alternativeSlot;
                    return actions;
                }

                if (alternativeSlot.GatheringChance > 0)
                {
                    if (alternativeSlot.GatheringChance >= 95 &&
                        CanUseAction(EAction.SharpVision1, EAction.FieldMastery1))
                    {
                        _slotToGather = alternativeSlot;
                        logger.LogDebug("GatheringChance != 100, >= 95, using SharpVision1/FieldMastery1");
                        actions.Enqueue(PickAction(EAction.SharpVision1, EAction.FieldMastery1));
                        return actions;
                    }

                    if (alternativeSlot.GatheringChance >= 85 &&
                        CanUseAction(EAction.SharpVision2, EAction.FieldMastery2))
                    {
                        _slotToGather = alternativeSlot;
                        logger.LogDebug("GatheringChance != 100, >= 85, using SharpVision2/FieldMastery2");
                        actions.Enqueue(PickAction(EAction.SharpVision2, EAction.FieldMastery2));
                        return actions;
                    }

                    if (alternativeSlot.GatheringChance >= 50 &&
                        CanUseAction(EAction.SharpVision3, EAction.FieldMastery3))
                    {
                        _slotToGather = alternativeSlot;
                        logger.LogDebug("GatheringChance != 100, >= 50, using SharpVision3/FieldMastery3");
                        actions.Enqueue(PickAction(EAction.SharpVision3, EAction.FieldMastery3));
                        return actions;
                    }
                }
            }

            SlotInfo? slot = slots.SingleOrDefault(x => x.ItemId == Task.Request.ItemId);
            if (slot == null)
            {
                if (!_usedLuck &&
                    nodeCondition.CurrentIntegrity == nodeCondition.MaxIntegrity &&
                    CanUseAction(EAction.LuckOfTheMountaineer, EAction.LuckOfThePioneer))
                {
                    _usedLuck = true;
                    logger.LogDebug("Using Luck");
                    actions.Enqueue(PickAction(EAction.LuckOfTheMountaineer, EAction.LuckOfThePioneer));
                    return actions;
                }
                else if (_usedLuck)
                {
                    // we still can't find the item, if this node has been hit at least once we just close it
                    logger.LogDebug("Didn't find item after using Luck, moving on...");
                    if (nodeCondition.CurrentIntegrity != nodeCondition.MaxIntegrity)
                    {
                        return null;
                    }

                    logger.LogDebug("Actually there's crystals, let's get those");
                    // otherwise, there most likely is -any- other item available, probably a shard/crystal
                    _slotToGather = slots.MinBy(x => x.ItemId);
                    return actions;
                }
            }

            slot = slots.SingleOrDefault(x => x.ItemId == Task.Request.ItemId);
            if (slot is { GatheringChance: > 0 and < 100 })
            {
                if (slot.GatheringChance >= 95 &&
                    CanUseAction(EAction.SharpVision1, EAction.FieldMastery1))
                {
                    logger.LogDebug("GatheringChance != 100, >= 95, using SharpVision1/FieldMastery1");
                    actions.Enqueue(PickAction(EAction.SharpVision1, EAction.FieldMastery1));
                    return actions;
                }

                if (slot.GatheringChance >= 85 &&
                    CanUseAction(EAction.SharpVision2, EAction.FieldMastery2))
                {
                    logger.LogDebug("GatheringChance != 100, >= 85, using SharpVision1/FieldMastery1");
                    actions.Enqueue(PickAction(EAction.SharpVision2, EAction.FieldMastery2));
                    return actions;
                }

                if (slot.GatheringChance >= 50 &&
                    CanUseAction(EAction.SharpVision3, EAction.FieldMastery3))
                {
                    logger.LogDebug("GatheringChance != 100, >= 50, using SharpVision1/FieldMastery1");
                    actions.Enqueue(PickAction(EAction.SharpVision3, EAction.FieldMastery3));
                    return actions;
                }
            }
            //}

            return actions;
        }

        private unsafe EAction PickAction(EAction minerAction, EAction botanistAction)
        {
            if ((Job?)PlayerState.Instance()->CurrentClassJobId == Job.MIN)
            {
                return minerAction;
            }
            else
            {
                return botanistAction;
            }
        }

        private unsafe bool CanUseAction(EAction minerAction, EAction botanistAction)
        {
            EAction action = PickAction(minerAction, botanistAction);
            return ActionManager.Instance()->GetActionStatus(ActionType.Action, (uint)action) == 0;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Local")]
    private sealed record SlotInfo(int Index, uint ItemId, int GatheringChance, int BoonChance, int Quantity);

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Local")]
    private sealed record NodeCondition
    (
        uint CurrentIntegrity,
        uint MaxIntegrity);
}
