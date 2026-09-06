using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Questionable.Utils;

/// <summary>
/// <c>ClassJob.ExpArrayIndex</c> 的邊界守衛：判斷它可不可以拿來索引
/// <c>PlayerState.ClassJobLevels</c> 與 <c>PlayerState.ClassJobExperience</c>。
/// </summary>
/// <remarks>
/// 🔴 <c>ExpArrayIndex</c> 是 <c>sbyte</c>，而第 0 列（冒險者/ADV）是 <b>-1</b>
/// （2026-09-07 離線確認台服 7.20 的 <c>ClassJob</c> 表 46 列裡只有第 0 列是負的，最大值 31）。
/// 那兩個欄位都是 <c>FixedSizeArray35</c>，產生出來的存取子是 <c>Span</c>，
/// 索引 -1 會擲 <see cref="System.IndexOutOfRangeException"/>。
/// <br/><br/>
/// 📌 越界時回「未知」而不是退回索引 0：陣列第 0 格是格鬥士/武僧（PGL/MNK），
/// 拿它當未知職業的等級是安靜的錯答案。台服共有 5 列的 <c>ExpArrayIndex</c> 是 0
/// （PGL、MNK，以及 43/44/45 三列空佔位）。
/// <br/><br/>
/// 📌 上界用 <c>Span.Length</c> 而不是寫死 35：長度是 FFXIVClientStructs 的定義，會隨改版變動。
/// </remarks>
internal static class ExpArrayIndexUtils
{
    private static readonly HashSet<uint> LoggedOutOfRange = [];
    private static readonly HashSet<uint> LoggedUnknownClassJob = [];

    /// <summary>
    /// <paramref name="expArrayIndex"/> 落在 <c>0 .. arrayLength - 1</c> 之內時回 <c>true</c>；
    /// 否則對同一個 ClassJob 只寫一行 <c>Information</c>，之後靜默，並回 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 用鎖而不是裸 <see cref="HashSet{T}"/>：呼叫點同時有 ImGui 繪製路徑
    /// （<c>ContextMenuController</c> 的選單建構）與框架執行緒，並行插入的失敗形式是集合本身壞掉，
    /// 不是「拿到舊值」。只有失敗路徑會進到這裡，鎖是無競爭的。
    /// 🔴 <paramref name="logger"/> 的呼叫刻意放在鎖外（鎖內不做 I/O）。
    /// 用 <c>Information</c> 是因為使用者回報用的 log 等級要收得到。
    /// </remarks>
    public static bool IsInRange(int expArrayIndex, int arrayLength, uint classJobId, ILogger logger)
    {
        if (expArrayIndex >= 0 && expArrayIndex < arrayLength)
            return true;

        bool firstTime;
        lock (LoggedOutOfRange)
            firstTime = LoggedOutOfRange.Add(classJobId);

        if (firstTime)
            logger.LogInformation(
                "ClassJob {ClassJobId} 的 ExpArrayIndex 是 {ExpArrayIndex}，不在 0..{MaxIndex} 之內（冒險者/ADV 是 -1）；以未知處理。",
                classJobId, expArrayIndex, arrayLength - 1);

        return false;
    }

    /// <summary>
    /// 查不到某個 ClassJob 的 <c>ExpArrayIndex</c> 時，對同一個 ClassJob 只寫一行 <c>Information</c>。
    /// </summary>
    public static void LogUnknownClassJobOnce(uint classJobId, ILogger logger)
    {
        bool firstTime;
        lock (LoggedUnknownClassJob)
            firstTime = LoggedUnknownClassJob.Add(classJobId);

        if (firstTime)
            logger.LogInformation(
                "ClassJob {ClassJobId} 查不到對應的 ExpArrayIndex（冒險者/ADV 與空佔位列都會落到這裡）；以未知處理。",
                classJobId);
    }
}
