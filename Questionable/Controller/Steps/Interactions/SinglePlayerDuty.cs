using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Questionable.Controller.Steps.Common;
using Questionable.Controller.Steps.Shared;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.Controller.Steps.Interactions;

internal static class SinglePlayerDuty
{
    internal static class SpecialTerritories
    {
        public const ushort Lahabrea = 1052;
        public const ushort ItsProbablyATrap = 665;
        public const ushort Naadam = 688;
        public const ushort Patisserie = 1298;
    }

    internal sealed class Factory
    (
        BossModIpc bossModIpc,
        TerritoryData territoryData,
        IObjectTable objectTable,
        ICondition condition,
        IClientState clientState) : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.SinglePlayerDuty)
            {
                yield break;
            }

            if (bossModIpc.IsConfiguredToRunSoloInstance(quest.Id, step.SinglePlayerDutyOptions))
            {
                uint cfcId = 0;
                uint tId = 0;
                TerritoryData.ContentFinderConditionData? cfcData = null;
                if (quest.Id.Value.Equals(5325))
                {
                    cfcId = 1045;
                    tId = 1298;
                }
                else if (!territoryData.TryGetContentFinderConditionForSoloInstance(quest.Id, step.SinglePlayerDutyIndex, out cfcData))
                {
                    throw new TaskException("Failed to get content finder condition for solo instance");
                }
                if (cfcData != null)
                {
                    cfcId = cfcData.ContentFinderConditionId;
                    tId = cfcData.TerritoryId;
                }

                yield return new Mount.UnmountTask();
                if (tId == SpecialTerritories.Patisserie)
                {
                    yield return new Commence(cfcId);
                }
                yield return new StartSinglePlayerDuty(cfcId);
                yield return new WaitAtStart.WaitDelay(TimeSpan.FromSeconds(2)); // maybe a delay will work here too, needs investigation
                if (tId == SpecialTerritories.Lahabrea)
                {
                    yield return new SetTarget(14643);
                    yield return new EnableAi();
                    yield return new WaitCondition.Task(
                        () => condition[ConditionFlag.Unconscious] || clientState.TerritoryType != SpecialTerritories.Lahabrea,
                        "Wait(death)");
                    yield return new DisableAi();
                    yield return new WaitCondition.Task(
                        () => !condition[ConditionFlag.Unconscious] || clientState.TerritoryType != SpecialTerritories.Lahabrea,
                        "Wait(resurrection)");
                    yield return new EnableAi();
                }
                else if (tId is SpecialTerritories.ItsProbablyATrap)
                {
                    yield return new WaitCondition.Task(() => DutyActionsAvailable() || clientState.TerritoryType != SpecialTerritories.ItsProbablyATrap,
                        "Wait(Phase 2)");
                    yield return new EnableAi(true);
                }
                else if (tId is SpecialTerritories.Naadam)
                {
                    yield return new WaitCondition.Task(
                        () =>
                        {
                            if (clientState.TerritoryType != SpecialTerritories.Naadam)
                            {
                                return true;
                            }

                            Vector3 pos = objectTable[0]?.Position ?? default;
                            return (new Vector3(352.01f, -1.45f, 288.59f) - pos).Length() < 10f;
                        },
                        "Wait(moving to Ovoo)");
                    yield return new Mount.UnmountTask();
                    yield return new EnableAi();
                }
                else if (tId == SpecialTerritories.Patisserie)
                {
                    yield return new SetPreset(BossModIpc.EPreset.NormalMovement);
                }
                else
                {
                    yield return new EnableAi(tId == SpecialTerritories.Naadam);
                }

                yield return new WaitSinglePlayerDuty(cfcId);
                yield return new DisableAi();
                yield return new WaitAtEnd.WaitNextStepOrSequence();
            }
        }

        private unsafe bool DutyActionsAvailable()
        {
            // 🔴 RaptureHotbarModule.Instance() 是手寫包裝，UIModule 還沒建好時合法回 null
            //    （Dalamud lib/FFXIVClientStructs/.../Client/UI/Misc/RaptureHotbarModule.cs:15-18）。
            //    原本 ->DutyActionsPresent 對 null 裸解參考＝AccessViolationException，
            //    在 .NET Core 是 corrupted-state exception，try/catch 攔不到。
            // fail-closed：回 false＝「任務技能還沒出現」。唯一呼叫點是上面 WaitCondition.Task("Wait(Phase 2)")
            //    的輪詢述詞，回 false 就是「繼續等」——正好是安全的退化行為；回 true 會讓流程誤判階段已推進
            //    而提早 EnableAi(true)。
            // 這是輪詢述詞（每次條件檢查都會呼叫），刻意不寫 log。
            RaptureHotbarModule* hotbarModule = RaptureHotbarModule.Instance();
            return hotbarModule != null && hotbarModule->DutyActionsPresent;
        }
    }

    internal sealed record StartSinglePlayerDuty(uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Wait(BossMod, entered instance {ContentFinderConditionId})";
        }
    }

    internal sealed class StartSinglePlayerDutyExecutor(ICondition condition) : TaskExecutor<StartSinglePlayerDuty>
    {
        private DateTime _enteredAt = DateTime.MinValue;

        protected override bool Start()
        {
            return true;
        }

        public override unsafe ETaskResult Update()
        {
            GameMain* gameMain = GameMain.Instance();
            if (gameMain->CurrentContentFinderConditionId != Task.ContentFinderConditionId)
            {
                return ETaskResult.StillRunning;
            }

            if (!condition[ConditionFlag.BoundByDuty])
            {
                return ETaskResult.StillRunning;
            }

            // we add a minimum wait time to try avoid issues with starting too early
            // could also be adding unnecessary wait time but needs more investigation ig
            if (_enteredAt == DateTime.MinValue)
            {
                _enteredAt = DateTime.Now;
            }

            return DateTime.Now - _enteredAt >= TimeSpan.FromSeconds(2)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed record EnableAi(bool Passive = false) : ITask
    {
        public override string ToString()
        {
            return $"BossMod.EnableAi({(Passive ? "Passive" : "AutoPull")})";
        }
    }

    internal sealed class EnableAiExecutor
    (
        BossModIpc bossModIpc) : TaskExecutor<EnableAi>
    {
        protected override bool Start()
        {
            bossModIpc.EnableAi(Task.Passive);
            return true;
        }

        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed record SetPreset(BossModIpc.EPreset Preset) : ITask
    {
        public override string ToString()
        {
            return $"BossMod.SetPreset({Enum.GetName(Preset)})";
        }
    }

    internal sealed class SetPresetExecutor
    (
        BossModIpc bossModIpc) : TaskExecutor<SetPreset>
    {
        protected override bool Start()
        {
            bossModIpc.SetPreset(Task.Preset);
            return true;
        }

        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed record WaitSinglePlayerDuty(uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Wait(BossMod, left instance {ContentFinderConditionId})";
        }
    }

    internal sealed class WaitSinglePlayerDutyExecutor
    (
        BossModIpc bossModIpc,
        MovementController movementController)
        : TaskExecutor<WaitSinglePlayerDuty>, IStoppableTaskExecutor, IDebugStateProvider
    {
        public string? GetDebugState()
        {
            if (!movementController.IsNavmeshReady)
            {
                return $"Navmesh: {movementController.BuiltNavmeshPercent}%";
            }
            else
            {
                return null;
            }
        }

        public override unsafe ETaskResult Update()
        {
            return GameMain.Instance()->CurrentContentFinderConditionId != Task.ContentFinderConditionId
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public void StopNow()
        {
            bossModIpc.DisableAi();
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
        protected override bool Start()
        {
            return true;
        }
    }

    internal sealed record DisableAi : ITask
    {
        public override string ToString()
        {
            return "BossMod.DisableAi";
        }
    }

    internal sealed class DisableAiExecutor
    (
        BossModIpc bossModIpc) : TaskExecutor<DisableAi>
    {
        protected override bool Start()
        {
            bossModIpc.DisableAi();
            return true;
        }

        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    // TODO this should be handled in VBM
    internal sealed record SetTarget(uint DataId) : ITask
    {
        public override string ToString()
        {
            return $"SetTarget({DataId})";
        }
    }

    internal sealed class SetTargetExecutor
    (
        ITargetManager targetManager,
        IObjectTable objectTable) : TaskExecutor<SetTarget>
    {
        protected override bool Start()
        {
            return true;
        }

        public override ETaskResult Update()
        {
            if (GameFunctions.GetBaseID(targetManager.Target) == Task.DataId)
            {
                return ETaskResult.TaskComplete;
            }

            IGameObject? gameObject = objectTable.FirstOrDefault(x => GameFunctions.GetBaseID(x) == Task.DataId);
            if (gameObject == null)
            {
                return ETaskResult.StillRunning;
            }

            targetManager.Target = gameObject;
            return ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    // TODO valentiones hack
    internal sealed record Commence(uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Commence({ContentFinderConditionId})";
        }
    }

    internal sealed class CommenceExecutor(ICondition condition) : TaskExecutor<Commence>
    {
        private DateTime _enteredAt = DateTime.MinValue;
        protected override bool Start()
        {
            return true;
        }

        public override unsafe ETaskResult Update()
        {
            if (GenericHelpers.TryGetAddonMaster(out AddonMaster.ContentsFinderConfirm m) && m.IsAddonReady)
            {
                // EzThrottler 記的是「上一次動作在哪個時刻」而不是「這扇窗按過了」，
                // 而且 key 全外掛共用、首次必放行 —— 真正擋住重按的是守衛。
                if (EzThrottler.Throttle("Confirm", 2000) &&
                    AddonPressGuard.TryBeginPress("ContentsFinderConfirm", m.Base, "commence"))
                {
                    m.Commence();
                }
            }
            GameMain* gameMain = GameMain.Instance();
            if (gameMain->CurrentContentFinderConditionId != Task.ContentFinderConditionId)
            {
                return ETaskResult.StillRunning;
            }

            if (!condition[ConditionFlag.BoundByDuty])
            {
                return ETaskResult.StillRunning;
            }
            if (_enteredAt == DateTime.MinValue)
            {
                _enteredAt = DateTime.Now;
            }

            return DateTime.Now - _enteredAt >= TimeSpan.FromSeconds(2)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }
}
