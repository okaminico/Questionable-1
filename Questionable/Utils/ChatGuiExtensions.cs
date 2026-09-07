using Dalamud.Plugin.Services;
using System.Collections.Concurrent;

namespace Questionable.Utils;

/// <summary>
/// 把聊天輸出釘在 framework 執行緒上。
/// </summary>
/// <remarks>
/// 🔴 <b>本 pin 的 <c>ChatGui</c> 內部是一個沒有任何同步的 <c>Queue&lt;XivChatEntry&gt;</c></b>
/// （<c>Dalamud/Game/Gui/ChatGui.cs:43</c>）：<c>Print</c>／<c>PrintError</c> 只是 <c>Enqueue</c>，
/// 而 <c>UpdateQueue()</c> 在 framework 執行緒上 <c>TryDequeue</c>
/// （<c>Dalamud/Game/Framework.cs:393</c> 每幀呼叫一次）。
/// <para>
/// 🔴 <b>IPC 端點跑在呼叫端外掛的執行緒上</b>，所以「從 IPC 端點可達的聊天輸出」＝
/// 另一條執行緒在 <c>Enqueue</c>、framework 執行緒同時在 <c>TryDequeue</c>。
/// <c>Queue&lt;T&gt;</c> 的失敗形式不是「少一則訊息」而是<b>佇列本身壞掉</b>——
/// <c>Enqueue</c> 滿了會 <c>Grow()</c> 換掉底層陣列，head／tail／size 也可能撕裂。
/// </para>
/// <para>
/// 📌 <c>IFramework.RunOnFrameworkThread(Action)</c> <b>已經在 framework 執行緒上時就地同步執行</b>
/// （<c>Framework.cs:173-181</c>），所以每幀那條正常路徑的行為完全沒有變；
/// 只有真的從別的執行緒進來的呼叫才會被排到下一幀。
/// </para>
/// <para>
/// 🔑 <b>為什麼還要自己排一個佇列，而不是每則訊息各自包成一個排程工作</b>：Dalamud 的
/// <c>ThreadBoundTaskScheduler</c> 用 <c>ConcurrentDictionary</c> 存待跑的工作、
/// <c>Run()</c> 走訪它的 <c>Keys</c>（<c>Dalamud/Utility/ThreadBoundTaskScheduler.cs:16,46-55</c>）
/// ⇒ <b>不保證先進先出</b>。各自包一個工作的話，同一格內送出的多則訊息順序會變成隨機的——
/// 而 <c>TaskCreator</c> 的「這個任務被停用了」是<b>連續三行、要照順序讀</b>的。
/// 改成自己用 <see cref="ConcurrentQueue{T}"/> 排隊、到了 framework 執行緒一次排乾，
/// 順序就與呼叫順序逐字相同。
/// </para>
/// </remarks>
internal static class ChatGuiExtensions
{
    /// <summary>還沒送出的訊息。<b>順序就是呼叫順序。</b></summary>
    private static readonly ConcurrentQueue<(IChatGui Chat, string Message, string? MessageTag, ushort? TagColor)>
        PendingErrors = new();

    /// <summary>
    /// 與 <c>IChatGui.PrintError(string, string?, ushort?)</c> 相同，但保證在 framework 執行緒上送出。
    /// </summary>
    /// <remarks>
    /// 📌 訊息內容在呼叫端的執行緒上就組好了（內插、tag、顏色都在進到這裡之前完成），
    /// 排隊的只是「送出」這個動作，所以看到的字一個都沒變。
    /// <para>
    /// 🔴 <b>刻意不等它跑完</b>：七個呼叫點都是「印一行就繼續做事」，沒有任何一處需要印完才能往下走。
    /// 代價是 <c>PrintError</c> 真的擲例外時會被吞進那個沒人看的 <c>Task</c>——
    /// 但排乾迴圈送出的不一定是這一次排進去的那一則，把它擲回呼叫端反而是錯的歸屬。
    /// </para>
    /// </remarks>
    public static void PrintErrorOnFrameworkThread(this IChatGui chatGui, IFramework framework,
        string message, string? messageTag = null, ushort? tagColor = null)
    {
        PendingErrors.Enqueue((chatGui, message, messageTag, tagColor));
        _ = framework.RunOnFrameworkThread(static () =>
        {
            while (PendingErrors.TryDequeue(out var pending))
            {
                pending.Chat.PrintError(pending.Message, pending.MessageTag, pending.TagColor);
            }
        });
    }
}
