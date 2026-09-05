using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Numerics;
namespace Questionable.Controller.Steps.Shared;

internal sealed class ExtraConditionUtils(IClientState clientState, IObjectTable objectTable)
{
    private readonly IClientState _clientState = clientState;
    private readonly IObjectTable _objectTable = objectTable;

    /// <summary>
    /// 已經回報過「尚未實作」的略過條件；用來讓同一個列舉值只寫一次診斷。
    /// </summary>
    /// <remarks>
    /// 刻意自帶集合與鎖，<b>不用</b> <c>ECommons.Throttlers.EzThrottler</c>：那個不是執行緒安全的，
    /// 而且是整個外掛共用的靜態實例，弄壞的話會連帶弄壞同一外掛內所有模組的節流。
    /// </remarks>
    private static readonly HashSet<EExtraSkipCondition> ReportedUnimplemented = [];

    private static readonly object ReportedUnimplementedLock = new();

    public bool MatchesExtraCondition(EExtraSkipCondition skipCondition)
    {
        Vector3? position = _objectTable[0]?.Position;
        return position != null &&
               _clientState.TerritoryType != 0 &&
               MatchesExtraCondition(skipCondition, position.Value, _clientState.TerritoryType);
    }

    public static bool MatchesExtraCondition(EExtraSkipCondition skipCondition, Vector3 position, uint territoryType)
    {
        return skipCondition switch
        {
            EExtraSkipCondition.WakingSandsMainArea => territoryType == 212 && position.X < 24,
            EExtraSkipCondition.WakingSandsSolar => territoryType == 212 && position.X >= 24,
            EExtraSkipCondition.RisingStonesSolar => territoryType == 351 && position.Z <= -28,
            EExtraSkipCondition.RoguesGuild => territoryType == 129 && position.Y <= -115,
            EExtraSkipCondition.NotRoguesGuild => territoryType == 129 && position.Y > -115,
            EExtraSkipCondition.DockStorehouse => territoryType == 137 && position.Y <= -20,
            var other => ReportUnimplemented(other)
        };
    }

    /// <summary>
    /// 處理尚未實作的略過條件：對每一個列舉值寫一次 <c>Information</c> 級診斷，然後回 <c>false</c>
    /// —— 也就是把這個略過條件一律當成「不成立」，任務步驟照常執行、不會被略過。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 這裡原本是擲 <see cref="ArgumentOutOfRangeException" />。2026-09-05 引用上游任務路徑資料之後，
    /// 資料裡出現了本地尚未實作的列舉值（<c>CostaDelSol</c>、<c>NewGamePlus</c>、<c>NotNewGamePlus</c>），
    /// 使得那些任務跑到這一步時會擲例外，被 <c>MiniTaskController</c> 的 catch 接住後記 Error、
    /// 印聊天錯誤並停止執行 —— 不是崩潰，但那些任務跑不動。改成回 <c>false</c> 之後，
    /// 最差的情況只是「本來該略過的步驟沒被略過」，任務仍會繼續跑。
    /// </para>
    /// <para>
    /// <b>NotNewGamePlus 刻意不在這裡猜著實作。</b>語意上「不是新周目」對一般玩家應該要是 <c>true</c>，
    /// 但那是推測而不是實測結果，猜錯會讓步驟被錯誤地略過（比「沒略過」更難查）。
    /// 讓它一樣走這條路，下面的 log-once 會在它真的出現時把問題浮出來，屆時再照實機情況補上正確實作。
    /// </para>
    /// </remarks>
    private static bool ReportUnimplemented(EExtraSkipCondition skipCondition)
    {
        bool firstTime;
        lock (ReportedUnimplementedLock)
        {
            firstTime = ReportedUnimplemented.Add(skipCondition);
        }

        if (firstTime)
        {
            Svc.Log.Information(
                $"[略過條件] 尚未實作的略過條件 {skipCondition}（列舉值 {(int)skipCondition}）：" +
                "這個條件一律當成不成立，也就是任務步驟不會因為它而被略過。" +
                "它來自新匯入的上游任務路徑資料，需要補上對應的判定邏輯。");
        }

        return false;
    }
}
