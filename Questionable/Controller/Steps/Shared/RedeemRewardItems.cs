using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
namespace Questionable.Controller.Steps.Shared;

internal static class RedeemRewardItems
{
    /// <summary>已經試過的道具：道具 id -> 動手當下的堆疊數量。只在本次執行期間有效，不寫檔。</summary>
    /// <remarks>
    /// 🔴 這張表是為了寶箱存在的：<c>CofferReward.IsUnlocked()</c> 恆為 false，
    /// 沒有它就會<b>每接一個任務重試一次</b>，背包滿的時候永遠不會停。
    /// 數量沒變＝同一疊，跳過；數量變了（又拿到新的一個）才會再試一次。
    /// <para>
    /// 🔑 用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而不是裸 <see cref="Dictionary{TKey,TValue}"/>：
    /// 目前所有寫入端都在 framework 執行緒上（唯一的建立路徑 <c>Factory.CreateAllTasks</c> 從 IPC
    /// 端點可達，但那些端點全部包在 <c>IpcFrameworkGate</c> 裡，21 個一個不漏），
    /// 所以裸字典今天也是對的——但那個不變式靠的是「以後每個新端點都記得包閘門」。
    /// 這裡不值得為了省一點點成本去賭那件事。
    /// </para>
    /// </remarks>
    private static readonly ConcurrentDictionary<uint, int> AttemptedItems = new();

    /// <summary>開始一輪新的自動化時清空，讓使用者「重新跑一次」等於「再試一次」。</summary>
    internal static void ResetAttemptedItems() => AttemptedItems.Clear();

    /// <summary>記下這一疊已經動過手了。只有寶箱會呼叫。</summary>
    internal static void RecordAttempt(uint itemId, int countBeforeUse) =>
        AttemptedItems[itemId] = countBeforeUse;

    internal sealed class Factory(QuestData questData, Configuration configuration) : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.AcceptQuest)
            {
                return [];
            }

            List<ITask> tasks = [];
            unsafe
            {
                InventoryManager* inventoryManager = InventoryManager.Instance();
                if (inventoryManager == null)
                {
                    return tasks;
                }

                foreach(ItemReward itemReward in questData.RedeemableItems)
                {
                    bool isCoffer = itemReward.Type is EItemRewardType.Coffer;

                    // 預設關：本 fork 沒有上游那個黑名單，開了就沒辦法排除想留著的箱子。
                    // 關著的時候連背包都不用查（台服符合條件的寶箱有 236 件）。
                    if (isCoffer && !configuration.Advanced.AutoRedeemCoffers)
                    {
                        continue;
                    }

                    int count = inventoryManager->GetInventoryItemCount(itemReward.ItemId);
                    if (count <= 0 || itemReward.IsUnlocked())
                    {
                        continue;
                    }

                    // 寶箱的 IsUnlocked() 恆為 false，同一疊試過就不再試。
                    if (isCoffer &&
                        AttemptedItems.TryGetValue(itemReward.ItemId, out int attemptedCount) &&
                        attemptedCount == count)
                    {
                        continue;
                    }

                    tasks.Add(new Task(itemReward));
                }
            }

            return tasks;
        }
    }

    internal sealed record Task(ItemReward ItemReward) : ITask
    {
        public override string ToString()
        {
            return $"TryRedeem({ItemReward.Name})";
        }
    }

    internal sealed class Executor
    (
        GameFunctions gameFunctions,
        ICondition condition) : TaskExecutor<Task>
    {
        private static readonly TimeSpan MinimumCastTime = TimeSpan.FromSeconds(4);
        private DateTime _continueAt;

        protected override unsafe bool Start()
        {
            if (condition[ConditionFlag.Mounted])
            {
                return false;
            }

            // 寶箱要有空格才開得起來；沒空格就放棄這一件，不要卡在重試上。
            bool isCoffer = Task.ItemReward.Type is EItemRewardType.Coffer;
            int countBeforeUse = 0;
            if (isCoffer)
            {
                if (GameFunctions.GetFreeInventorySlots() < 1)
                {
                    return false;
                }

                InventoryManager* inventoryManager = InventoryManager.Instance();
                if (inventoryManager == null)
                {
                    return false;
                }

                countBeforeUse = inventoryManager->GetInventoryItemCount(Task.ItemReward.ItemId);
            }

            TimeSpan castTime = Task.ItemReward.CastTime;
            if (castTime < MinimumCastTime)
            {
                castTime = MinimumCastTime;
            }

            _continueAt = DateTime.Now
                .Add(castTime)
                .AddSeconds(3);
            if (!gameFunctions.UseItem(Task.ItemReward.ItemId))
            {
                return false;
            }

            // 🔴 寶箱的 IsUnlocked() 恆為 false，不記下來就會每接一個任務重試一次。
            if (isCoffer)
            {
                RecordAttempt(Task.ItemReward.ItemId, countBeforeUse);
            }

            return true;
        }

        public override ETaskResult Update()
        {
            if (condition[ConditionFlag.Casting])
            {
                return ETaskResult.StillRunning;
            }

            return DateTime.Now <= _continueAt ? ETaskResult.StillRunning : ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return true;
        }
    }
}
