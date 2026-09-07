using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Questionable.External;

/// <summary>
/// IPC 端點的「遊戲主執行緒閘門」。
/// </summary>
/// <remarks>
/// 🔴🔴 為什麼需要這一層：Dalamud 的 CallGate 是<b>直接方法呼叫</b>，提供端的碼跑在
/// <b>呼叫端的執行緒</b>上。別的外掛從自己的背景工作、<c>Task.Run</c>、或任何非 framework
/// 執行緒打過來時，Questionable 這一側就會在那條執行緒上
/// ①讀遊戲的原生記憶體（<c>QuestManager.Instance()-&gt;</c>、<c>PlayerState.Instance()-&gt;</c>、
/// <c>_objectTable[0]</c>、<c>StatusManager.ExecuteStatusOff</c>）
/// ②寫任務佇列與優先任務清單（<c>TaskQueue._tasks</c> 與
/// <c>QuestController.ManualPriorityQuests</c> 都是裸 <see cref="List{T}"/>）。
/// <para>
/// ① 的失敗形式是 <c>AccessViolationException</c>——那在 .NET Core 是 corrupted-state
/// exception，<c>try</c>/<c>catch</c> 攔不到，整個遊戲直接崩掉。
/// ② 的失敗形式不是「少一筆」而是<b>集合本身壞掉</b>：<c>List&lt;T&gt;.Add</c> 滿了會換掉底層
/// 陣列，而 UI 每幀在繪製執行緒上迭代同一批清單。
/// </para>
/// <para>
/// 🔑 所以凡是「同步可達原生記憶體或共用集合」的端點，一律把<b>整個方法體</b>交回主執行緒執行
/// ——不是只有第一行檢查，這樣連下游的 helper 也一起被覆蓋，不必逐一追。
/// </para>
/// <para>
/// 📌 <b>已經在主執行緒上呼叫時行為逐字不變</b>：直接就地執行，不配置 <see cref="Task"/>、
/// 不改變例外型別、不多花任何一幀。絕大多數消費端（別的外掛在自己的 <c>Framework.Update</c>
/// 或 <c>TaskManager</c> 裡呼叫）走的就是這條路。
/// </para>
/// <para>
/// ⚠️ 逾時的處置：等主執行緒最多 <see cref="TimeoutMs"/> 毫秒。逾時就回該端點的「不可用」值
/// （false／null／空字串／空清單），語意與「現在做不到」相同——那些值原本就在端點的失敗路徑上
/// 出現過，呼叫端本來就要處理。同時用 <see cref="Interlocked"/> 把還沒開始跑的工作標成放棄，
/// 避免「呼叫端已經拿到 false 走人了，五秒後任務才真的被啟動」這種無人值守的形狀。
/// </para>
/// <para>
/// 🔴 用 <c>RunOnFrameworkThread</c> 不是 <c>Framework.Run</c>：前者在已經是主執行緒時就地執行，
/// 同步等它不會死結；後者一律 <c>StartNew</c>，同步等會死結。
/// </para>
/// <para>
/// 📌 這一份是 <c>Lifestream/IPC/IpcFrameworkGate.cs</c> 的移植；差別只在 Questionable 走 DI
/// （<see cref="IFramework"/> 與 <see cref="ILogger"/> 都是注入的），不是 ECommons 的 <c>Svc</c>。
/// </para>
/// </remarks>
internal sealed class IpcFrameworkGate
{
    /// <summary>等主執行緒的上限。超過就當作「現在做不到」。</summary>
    internal const int TimeoutMs = 5000;

    private const int StatePending = 0;
    private const int StateRunning = 1;
    private const int StateAbandoned = 2;

    /// <summary>同一個端點的逾時訊息重印間隔。</summary>
    private const long TimeoutLogIntervalMs = 10000;

    /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
    private const int MaxTrackedTimeoutKeys = 128;

    private readonly IFramework _framework;
    private readonly ILogger<IpcFrameworkGate> _logger;
    private readonly Dictionary<string, long> _timeoutLogTimes = [];

    public IpcFrameworkGate(IFramework framework, ILogger<IpcFrameworkGate> logger)
    {
        _framework = framework;
        _logger = logger;
    }

    /// <summary>有回傳值的端點。<paramref name="unavailable"/> 是逾時時要回的「不可用」值。</summary>
    public T Get<T>(string endpoint, Func<T> body, T unavailable)
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            return body();
        }

        int state = StatePending;
        Task<T> task = _framework.RunOnFrameworkThread(() =>
        {
            // 呼叫端已經逾時走人了就什麼都不做。
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending)
            {
                return unavailable;
            }

            return body();
        });

        // WaitAny 對已經失敗的工作也回 0（不擲），交給 GetResult 原樣重擲原始例外，
        // 這樣呼叫端看到的例外型別與沒有這層閘門時完全一樣（不會變成 AggregateException）。
        if (Task.WaitAny([task], TimeoutMs) == 0)
        {
            return task.GetAwaiter().GetResult();
        }

        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
        return unavailable;
    }

    /// <summary>沒有回傳值的端點。</summary>
    public void Run(string endpoint, Action body)
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            body();
            return;
        }

        int state = StatePending;
        Task task = _framework.RunOnFrameworkThread(() =>
        {
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending)
            {
                return;
            }

            body();
        });

        if (Task.WaitAny([task], TimeoutMs) == 0)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
    }

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="TimeoutLogIntervalMs"/> 毫秒放行一次。
    /// </summary>
    /// <remarks>
    /// 🔴 這裡刻意<b>不用</b> <c>EzThrottler</c>：它是整個外掛共用的靜態 <see cref="Dictionary{TKey,TValue}"/>
    /// 且零同步，而這條路徑跑在呼叫端的執行緒上，並行插入弄壞的是整張表——連帶弄壞外掛裡
    /// 所有模組的節流。所以自帶字典＋自己的鎖。
    /// 🔴 鎖內只碰字典——不寫記錄、不做 I/O、不呼叫任何別的外掛。
    /// </remarks>
    private bool ShouldLogTimeout(string key)
    {
        long now = Environment.TickCount64;
        lock(_timeoutLogTimes)
        {
            if (_timeoutLogTimes.TryGetValue(key, out long last) && now - last < TimeoutLogIntervalMs)
            {
                return false;
            }

            if (_timeoutLogTimes.Count >= MaxTrackedTimeoutKeys && !_timeoutLogTimes.ContainsKey(key))
            {
                _timeoutLogTimes.Clear();
            }

            _timeoutLogTimes[key] = now;
            return true;
        }
    }

    /// <summary>要使用者回報的診斷寫 Information（使用者的 LogLevel 收得到，也不會被 Debug 淹沒）。</summary>
    private void ReportTimeout(string endpoint, bool abandoned)
    {
        if (!ShouldLogTimeout(endpoint))
        {
            return;
        }

        string outcome = abandoned
            ? "工作還沒開始就被取消，什麼都沒做"
            : "工作已經開始執行，會照常跑完（呼叫端拿到的回值不代表它沒發生）";
        _logger.LogInformation(
            "[Questionable IPC 閘門] {Endpoint} 等待遊戲主執行緒超過 {TimeoutMs} 毫秒，已回傳「不可用」值。{Outcome}。" +
            "通常代表遊戲正在讀取畫面或嚴重掉幀；若持續出現請回報。",
            endpoint, TimeoutMs, outcome);
    }
}
