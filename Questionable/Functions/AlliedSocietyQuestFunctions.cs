using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Questing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
namespace Questionable.Functions;

internal sealed class AlliedSocietyQuestFunctions
{
    private readonly Dictionary<EAlliedSociety, byte> _alliedSocietyLastSeenRank = [];
    private readonly Dictionary<(uint NpcDataId, byte Seed, bool OutranksAll, bool RankedUp), List<QuestId>> _dailyQuests = [];
    private readonly ILogger<AlliedSocietyQuestFunctions> _logger;
    private readonly QuestData _questData;
    private readonly Dictionary<EAlliedSociety, List<NpcData>> _questsByAlliedSociety = [];

    /// <summary>還沒寫出去的記錄，見 <see cref="FlushPendingLogs"/>。</summary>
    private readonly ConcurrentQueue<(EAlliedSociety Tribe, byte Seed, uint IssuerId, string Quests)> _pendingLogs =
        new();

    /// <summary>延後清單的上限；正常情況一天只會進幾筆，滿了就不再收。</summary>
    private const int MaxPendingLogs = 64;

    public AlliedSocietyQuestFunctions(QuestData questData, ILogger<AlliedSocietyQuestFunctions> logger)
    {
        _questData = questData;
        _logger = logger;
        Initialize();
    }

    public void Initialize()
    {
        foreach(EAlliedSociety alliedSociety in Enum.GetValues<EAlliedSociety>().Where(x => x != EAlliedSociety.None))
        {
            List<QuestInfo> allQuests = _questData.GetAllByAlliedSociety(alliedSociety);
            Dictionary<uint, List<QuestInfo>> questsByIssuer = allQuests
                .Where(x => x.IsRepeatable)
                .GroupBy(x => x.IssuerDataId)
                .ToDictionary(x => x.Key,
                    x => x.OrderBy(y => y.AlliedSocietyQuestGroup == 3).ThenBy(y => y.QuestId).ToList());
            foreach((uint issuerDataId, List<QuestInfo> quests) in questsByIssuer)
            {
                NpcData npcData = new()
                    { IssuerDataId = issuerDataId, AllQuests = quests };
                if (_questsByAlliedSociety.TryGetValue(alliedSociety, out List<NpcData>? existingNpcs))
                {
                    existingNpcs.Add(npcData);
                }
                else
                {
                    _questsByAlliedSociety[alliedSociety] = [npcData];
                }
            }
        }
    }

    public void Reload()
    {
        foreach((uint NpcDataId, byte Seed, bool OutranksAll, bool RankedUp) item in _dailyQuests.Keys)
        {
            _dailyQuests.Remove(item);
        }
    }

    public unsafe List<QuestId> GetAvailableAlliedSocietyQuests(EAlliedSociety alliedSociety)
    {
        byte rankData = QuestManager.Instance()->BeastReputation[(int)alliedSociety - 1].Rank;
        byte currentRank = (byte)(rankData & 0x7F);
        if (currentRank == 0)
        {
            return [];
        }

        bool rankedUp = (rankData & 0x80) != 0;
        byte seed = QuestManager.Instance()->DailyQuestSeed;
        List<QuestId> result = [];
        foreach(NpcData npcData in _questsByAlliedSociety[alliedSociety])
        {
            bool outranksAll = npcData.AllQuests.All(x => currentRank > x.AlliedSocietyRank);
            (uint NpcDataId, byte seed, bool outranksAll, bool rankedUp) key = (NpcDataId: npcData.IssuerDataId, seed, outranksAll, rankedUp);
            bool rankChanged = _alliedSocietyLastSeenRank.ContainsKey(alliedSociety) && _alliedSocietyLastSeenRank[alliedSociety] != currentRank;
            if (rankChanged)
            {
                Reload();
            }
            if (_dailyQuests.TryGetValue(key, out List<QuestId>? questIds))
            {
                result.AddRange(questIds);
            }
            else
            {
                List<QuestId> quests = CalculateAvailableQuests(npcData.AllQuests, seed, outranksAll, currentRank, rankedUp);

                // 🔴 這一支從 QuestController 持著 _progressLock 的路徑可達
                //    （IsReadyToAcceptQuest -> IsDailyAlliedSocietyQuestAndAvailableToday -> 這裡），
                //    而 ILogger 最後落到 Serilog sink（自己有鎖、還會做檔案 I/O）。
                //    這條鏈太深、IsReadyToAcceptQuest 的呼叫端又太多，把 defer 一路傳下去會動到十幾處
                //    ⇒ 改成先收進佇列，由 DalamudInitializer 每幀在所有鎖外面排乾。
                if (_pendingLogs.Count < MaxPendingLogs)
                {
                    _pendingLogs.Enqueue((alliedSociety, seed, npcData.IssuerDataId, string.Join(", ", quests)));
                }

                _dailyQuests[key] = quests;
                result.AddRange(quests);
                _alliedSocietyLastSeenRank[alliedSociety] = currentRank;
            }
        }

        return result;
    }

    /// <summary>把鎖內攢下來的記錄寫出去。<b>必須在沒有持任何鎖的時候呼叫。</b></summary>
    /// <remarks>
    /// 📌 訊息的每一個參數在<b>加進佇列的那一刻</b>就求好值了（包括那個 <c>string.Join</c>），
    /// 所以延後寫出去的內容與「當場寫」逐字相同。
    /// </remarks>
    public void FlushPendingLogs()
    {
        while(_pendingLogs.TryDequeue(out (EAlliedSociety Tribe, byte Seed, uint IssuerId, string Quests) entry))
        {
            _logger.LogInformation("Available for {Tribe} (Seed: {Seed}, Issuer: {IssuerId}): {Quests}",
                entry.Tribe, entry.Seed, entry.IssuerId, entry.Quests);
        }
    }

    private static List<QuestId> CalculateAvailableQuests(List<QuestInfo> allQuests, byte seed, bool outranksAll,
        byte currentRank, bool rankedUp)
    {
        List<QuestInfo> eligible = [.. allQuests.Where(q => IsEligible(q, currentRank, rankedUp))];
        List<QuestInfo> available = [];
        if (eligible.Count == 0)
        {
            return [];
        }

        Rng rng = new(seed);
        if (outranksAll)
        {
            for(int i = 0, cnt = Math.Min(eligible.Count, 3); i < cnt; ++i)
            {
                int index = rng.Next(eligible.Count);
                while(available.Contains(eligible[index]))
                {
                    index = (index + 1) % eligible.Count;
                }
                available.Add(eligible[index]);
            }
        }
        else
        {
            int firstExclusive = eligible.FindIndex(q => q.AlliedSocietyQuestGroup == 3);
            if (firstExclusive >= 0)
            {
                available.Add(eligible[firstExclusive + rng.Next(eligible.Count - firstExclusive)]);
            }
            else
            {
                firstExclusive = eligible.Count;
            }
            for(int i = available.Count, cnt = Math.Min(firstExclusive, 3); i < cnt; ++i)
            {
                int index = rng.Next(firstExclusive);
                while(available.Contains(eligible[index]))
                {
                    index = (index + 1) % firstExclusive;
                }
                available.Add(eligible[index]);
            }
        }

        return available.Select(x => (QuestId)x.QuestId).ToList();
    }

    private static bool IsEligible(QuestInfo questInfo, byte currentRank, bool rankedUp)
    {
        return rankedUp ? questInfo.AlliedSocietyRank == currentRank : questInfo.AlliedSocietyRank <= currentRank;
    }

    private sealed class NpcData
    {
        public required uint IssuerDataId { get; init; }
        public required List<QuestInfo> AllQuests { get; init; } = [];
    }

    private record struct Rng(uint S0, uint S1 = 0, uint S2 = 0, uint S3 = 0)
    {
        public int Next(int range)
        {
            (S0, S1, S2, S3) = (S3, Transform(S0, S1), S1, S2);
            return (int)(S1 % range);
        }

        // returns new value for s1
        private static uint Transform(uint s0, uint s1)
        {
            uint temp = s0 ^ (s0 << 11);
            return s1 ^ temp ^ ((temp ^ (s1 >> 11)) >> 8);
        }
    }
}
