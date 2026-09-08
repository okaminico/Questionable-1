using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
// Lumina.Excel.Sheets 也有一個 Quest，不下別名整個檔的 Quest 引用會變成模糊。
using Quest = Questionable.Model.Quest;
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
    /// <remarks>
    /// 登出時也會清一次（<c>QuestController.ClearRedeemAttemptsOnLogout</c>）：這張表的鍵只有道具 id，
    /// 而「用不掉」的理由多半是角色自己的（已經學過那個表情、背包滿、等級不夠），
    /// 換角色之後前一個角色的結論不該繼續套用在新角色身上。
    /// </remarks>
    /// <returns>清掉的筆數。</returns>
    internal static int ResetAttemptedItems()
    {
        int cleared = AttemptedItems.Count;
        AttemptedItems.Clear();
        return cleared;
    }

    /// <summary>記下這一疊已經動過手了。只有寶箱會呼叫。</summary>
    internal static void RecordAttempt(uint itemId, int countBeforeUse) =>
        AttemptedItems[itemId] = countBeforeUse;

    internal sealed class Factory(QuestData questData, Configuration configuration, IDataManager dataManager)
        : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.AcceptQuest)
            {
                return [];
            }

            List<ITask> tasks = [];
            HashSet<uint> blacklist = configuration.Advanced.AutoRedeemItemBlacklist;
            unsafe
            {
                InventoryManager* inventoryManager = InventoryManager.Instance();
                if (inventoryManager == null)
                {
                    return tasks;
                }

                // 候選＝任務獎勵表 ∪ 背包裡本身就可兌換的道具。
                // 用道具 id 當鍵去重：ItemReward 是 record，但它包的 ItemRewardDetails
                // 是一般類別（參考相等），所以 record 自帶的相等性擋不掉重複。
                Dictionary<uint, ItemReward> candidates = [];
                foreach(ItemReward itemReward in questData.RedeemableItems)
                {
                    candidates.TryAdd(itemReward.ItemId, itemReward);
                }

                // 全背包掃描：任務獎勵表只涵蓋「有任務把它列為獎勵」的道具，
                // 從別處拿到的坐騎笛、寶箱、表情教材都不在裡面。
                // 只在 AcceptQuest 那一步跑一次，不是每幀。
                ExcelSheet<Item> itemSheet = dataManager.GetExcelSheet<Item>();
                for(InventoryType inventoryType = InventoryType.Inventory1;
                    inventoryType <= InventoryType.Inventory4;
                    ++inventoryType)
                {
                    InventoryContainer* container = inventoryManager->GetInventoryContainer(inventoryType);
                    if (container == null)
                    {
                        continue;
                    }

                    for(int i = 0; i < container->Size; ++i)
                    {
                        InventoryItem* slot = container->GetInventorySlot(i);
                        if (slot == null || slot->ItemId == 0)
                        {
                            continue;
                        }

                        uint itemId = slot->ItemId;
                        if (candidates.ContainsKey(itemId) || blacklist.Contains(itemId))
                        {
                            continue;
                        }

                        if (itemSheet.GetRowOrDefault(itemId) is not { } item)
                        {
                            continue;
                        }

                        // ⚠️ 這件不是從任務獎勵表來的，沒有對應的任務；
                        // ElementId 只用於介面顯示，這裡放 QuestId(0) 當佔位，不要拿它去查任務。
                        if (ItemReward.CreateFromItem(item, new QuestId(0)) is { } redeemable)
                        {
                            candidates.Add(itemId, redeemable);
                        }
                    }
                }

                foreach(ItemReward itemReward in candidates.Values)
                {
                    // 黑名單：使用者明確說「這件不要自動用」。所有型別都適用，不只寶箱。
                    if (blacklist.Contains(itemReward.ItemId))
                    {
                        continue;
                    }

                    bool isCoffer = itemReward.Type is EItemRewardType.Coffer;

                    // 預設關：寶箱沒有「已解鎖」狀態，開了就分辨不出哪一個是使用者想留的。
                    // （台服符合條件的寶箱有 236 件）
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
