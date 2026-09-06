using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
namespace Questionable.External;

internal sealed class NavmeshIpc(IDalamudPluginInterface pluginInterface, ILogger<NavmeshIpc> logger) : IDisposable
{
    private readonly ICallGateSubscriber<float> _buildProgress = pluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
    private readonly ICallGateSubscriber<bool> _isNavReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    private readonly ILogger<NavmeshIpc> _logger = logger;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> _navPathfind =
        pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>(
            "vnavmesh.Nav.PathfindCancelable");
    private readonly ICallGateSubscriber<bool> _pathIsRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    private readonly ICallGateSubscriber<List<Vector3>> _pathListWaypoints = pluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> _pathMoveTo = pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
    private readonly ICallGateSubscriber<float, object> _pathSetTolerance = pluginInterface.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance");

    // ── vnavmesh 移動租約端點（vnavmesh 7.20.0.37 起提供）─────────────────────
    // 🔴 這四支是**純新增**：上面的 vnavmesh.Path.SetTolerance 一個字都沒改。提供端沒有
    //    這幾支時（vnavmesh 沒裝，或版本太舊）會擲 IpcNotReadyError，我們逐字退回既有的
    //    全域寫入 —— 換新名字而不是同名改型別，正是為了讓舊/新兩邊都能乾淨落回 fail-safe。
    private readonly ICallGateSubscriber<string, int, Guid> _acquireSuppressionFor =
        pluginInterface.GetIpcSubscriber<string, int, Guid>("vnavmesh.Path.AcquireSuppressionFor");
    private readonly ICallGateSubscriber<Guid, int, bool> _renewSuppressionFor =
        pluginInterface.GetIpcSubscriber<Guid, int, bool>("vnavmesh.Path.RenewSuppressionFor");
    private readonly ICallGateSubscriber<Guid, bool> _releaseSuppression =
        pluginInterface.GetIpcSubscriber<Guid, bool>("vnavmesh.Path.ReleaseSuppression");
    private readonly ICallGateSubscriber<Guid, float, bool> _setLeasedTolerance =
        pluginInterface.GetIpcSubscriber<Guid, float, bool>("vnavmesh.Path.SetLeasedTolerance");
    private readonly ICallGateSubscriber<object> _pathStop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _queryPointOnFloor =
        pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _simpleMovePathfindAndMoveCloseTo =
        pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
    private readonly ICallGateSubscriber<Vector3, bool, bool> _simpleMovePathfindAndMoveTo =
        pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
    private readonly ICallGateSubscriber<bool> _simpleMovePathfindInProgress =
        pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");

    public bool IsReady
    {
        get
        {
            try
            {
                return _isNavReady.InvokeFunc();
            }
            catch(IpcError)
            {
                return false;
            }
        }
    }

    public bool IsPathRunning
    {
        get
        {
            try
            {
                return _pathIsRunning.InvokeFunc();
            }
            catch(IpcError)
            {
                return false;
            }
        }
    }

    public bool IsSimpleMovePathfindInProgress
    {
        get
        {
            try
            {
                return _simpleMovePathfindInProgress.InvokeFunc();
            }
            catch(IpcError)
            {
                return false;
            }
        }
    }

    public void Stop()
    {
        try
        {
            _pathStop.InvokeAction();
        }
        catch(IpcNotReadyError)
        {
            // vnavmesh 沒註冊 IPC。Stop() 每次 MoveTo() 前都會被呼叫,
            // 沒裝 vnavmesh 時照原本的寫法會變成每次移動都印一行警告。
        }
        catch(IpcError e)
        {
            _logger.LogWarning(e, "Could not stop navigating via navmesh");
        }
    }

    public Task<List<Vector3>> Pathfind(Vector3 localPlayerPosition, Vector3 targetPosition, bool fly,
        CancellationToken cancellationToken)
    {
        try
        {
            IExposedPlugin? plugin = pluginInterface.InstalledPlugins.FirstOrDefault(x =>
                x.InternalName == "vnavmesh" && x.IsLoaded);
            if (plugin != null && plugin.Version < new Version(1, 2, 3, 2))
            {
                throw new IpcValueNullError("vnavmesh", typeof(Version), 0);
            }
            // 🔑 租約優先：拿到就只影響 Questionable 自己這段移動，放約／逾時自動還原。
            // 🔴 拿不到（vnavmesh 沒裝，或版本早於 7.20.0.37）就退回既有的全域寫入 ——
            //    連它擲 IpcNotReadyError 進而讓這次 Pathfind() 失敗的行為都逐字保留。
            if (!TouchLease())
                _pathSetTolerance.InvokeAction(PathTolerance);
            return _navPathfind.InvokeFunc(localPlayerPosition, targetPosition, fly, cancellationToken);
        }
        catch(IpcNotReadyError e)
        {
            _logger.LogWarning(e, "Could not pathfind via navmesh");
            return Task.FromException<List<Vector3>>(e);
        }
        catch(IpcValueNullError e)
        {
            _logger.LogWarning(e, "Unsupported version of vnavmesh");
            return Task.FromException<List<Vector3>>(e);
        }
    }

    public void MoveTo(List<Vector3> position, bool fly)
    {
        Stop();

        // 這段路要跑多久事先不知道，把租約的閒置計時往後推。
        // 🔴 只在租約這條路可用時才有作用；退回全域寫入的那條路徑刻意**不**在這裡補寫一次
        //    容許值 —— 改動前 MoveTo() 從來不碰容許值，那條路徑要與改動前逐字相同。
        TouchLease();

        try
        {
            _pathMoveTo.InvokeAction(position, fly);
        }
        catch(IpcError e)
        {
            _logger.LogWarning(e, "Could not move via navmesh");
        }
    }

    public Vector3? GetPointOnFloor(Vector3 position, bool unlandable)
    {
        try
        {
            return _queryPointOnFloor.InvokeFunc(position, unlandable, 0.2f);
        }
        catch(IpcError)
        {
            return null;
        }
    }

    public bool SimplePathfindAndMoveTo(Vector3 destination, bool fly)
    {
        if (!IsReady)
        {
            return false;
        }
        try
        {
            return _simpleMovePathfindAndMoveTo.InvokeFunc(destination, fly);
        }
        catch(IpcError exception)
        {
            _logger.LogWarning(exception, "Could not SimplePathfindAndMoveTo");
            return false;
        }
    }

    public bool SimplePathfindAndMoveCloseTo(Vector3 destination, bool fly, float range)
    {
        if (!IsReady)
        {
            return false;
        }
        try
        {
            return _simpleMovePathfindAndMoveCloseTo.InvokeFunc(destination, fly, range);
        }
        catch(IpcError exception)
        {
            _logger.LogWarning(exception, "Could not SimplePathfindAndMoveCloseTo");
            return false;
        }
    }

    public List<Vector3> GetWaypoints()
    {
        if (IsPathRunning)
        {
            try
            {
                return _pathListWaypoints.InvokeFunc();
            }
            catch(IpcError)
            {
                return [];
            }
        }
        else
        {
            return [];
        }
    }

    public int GetBuildProgress()
    {
        try
        {
            float progress = _buildProgress.InvokeFunc();
            if (progress < 0)
            {
                return 100;
            }
            return (int)(progress * 100);
        }
        catch(IpcError)
        {
            return 0;
        }
    }

    // ══ vnavmesh 移動租約 ═══════════════════════════════════════════════════
    // 🔴🔴 改用租約的理由＝舊的開關沒有主人。
    // vnavmesh 的 FollowPath.Tolerance 是執行期的**全域**欄位，Path.SetTolerance 對它是
    // 單向寫入 —— 誰寫進去就一直停在那裡，而且它在 vnavmesh 裡**沒有任何使用者介面**，
    // 使用者既察覺不到、也改不回來。改動前 Questionable 每一次 Pathfind() 都寫一次 0.25
    // 且從不還原 ⇒ 別人（AutoDuty 的 MovementHelper 會針對最後一個路徑點設自己的值）
    // 設好的容許值會被我們靜默蓋掉並永久停在 0.25。
    //
    // 🔑 租約是**記名**的：只押自己那一把，放約或逾時就自動還原成使用者／別人的值，
    //    而且 vnavmesh 會在使用者的 log 寫一行 Information 指名是誰押著。
    //
    // 🔴 只在「真的在移動」的期間持有。永久押著會讓別的外掛的 Path.SetTolerance 在
    //    Questionable 載入期間**完全失效**（提供端的疊加是「租約值 ?? 使用者的值」），
    //    那比改動前更霸道。

    /// <summary>租約登記的名字，會出現在 vnavmesh 的 log 與租約快照裡。</summary>
    private const string LeaseOwner = "Questionable";

    /// <summary>每次取得／續約要求的租期（5 分鐘）＝提供端的硬性上限。</summary>
    /// <remarks>
    /// 🔴 不可以要求更長：提供端是<b>夾值不是拒絕</b>，要多了只會被靜默砍短，續約反而來不及。
    /// </remarks>
    private const int LeaseMilliseconds = 300_000;

    /// <summary>續約間隔（30 秒），是 <see cref="LeaseMilliseconds"/> 的十分之一。</summary>
    /// <remarks>
    /// ⚠️ 續約間隔<b>不能接近租期</b>：提供端 Renew 的第一件事是掃除已逾時的租約 ⇒ 間隔接近
    /// 租期時，第一次心跳送到那把已經被掃掉、續約<b>必定</b>回 false（不是競態，是每次都會發生）。
    /// </remarks>
    private const int RenewIntervalMilliseconds = 30_000;

    /// <summary>路徑停下來之後還要押住多久才交回租約（45 秒）。</summary>
    /// <remarks>
    /// 🔑 尋路是非同步的：Pathfind() 送出到 MoveTo() 真的開始跑之間，vnavmesh 的
    /// <c>Path.IsRunning</c> 是 false。沒有這段寬限期會在每兩段移動之間放掉又重拿一把。
    /// 🔴 <b>必須明顯大於尋路本身的上限</b>：<c>MovementController</c> 給尋路的取消期限是
    /// <b>30 秒</b>（<c>CancelAfter(TimeSpan.FromSeconds(30))</c>，兩處）。寬限期比它短的話，
    /// 每一次跑久一點的尋路都會在半路把租約放掉、等 MoveTo() 再重拿一把 —— 不會算錯，
    /// 但會在使用者的 log 上洗出成對的「已交回／已取得」。
    /// </remarks>
    private const int LeaseIdleGraceMilliseconds = 45_000;

    /// <summary>確認提供端沒有租約端點之後，隔多久再探測一次（60 秒）。</summary>
    /// <remarks>
    /// 🔑 讓使用者中途更新 vnavmesh 就能生效、不必重開遊戲，同時把失敗探測壓到每分鐘一次
    /// （沒有這個節流的話，沒裝 vnavmesh 時每一次 Pathfind() 都會多送一次必定失敗的 IPC）。
    /// </remarks>
    private const int LeaseProbeIntervalMilliseconds = 60_000;

    /// <summary>Questionable 要求的路徑容許值。改動前是寫死在 Pathfind() 裡的同一個值。</summary>
    private const float PathTolerance = 0.25f;

    /// <summary>保護下面四個租約欄位。</summary>
    /// <remarks>
    /// 🔴 <b>鎖內絕不呼叫 IPC、絕不做 I/O、絕不碰 ImGui</b>：IPC 會跑進 vnavmesh 的碼裡，
    /// 鎖內呼叫等於把別人的執行時間算進我們的臨界區。一律「鎖內取值／拍快照 → 出鎖 →
    /// 呼叫 IPC → 再進鎖寫回」。
    /// 🔴 <b>絕不用 ECommons 的 EzThrottler 做這裡的節流</b> —— 它是整個外掛共用的靜態
    /// Dictionary 且零同步，從 IPC 路徑碰它的失敗形式不是「拿到舊值」而是字典本身壞掉。
    /// 這裡自帶計時器（<see cref="Environment.TickCount64"/>）。
    /// </remarks>
    private readonly object _leaseGate = new();

    /// <summary>目前持有的租約；<see cref="Guid.Empty"/>＝沒有。</summary>
    private Guid _lease;

    /// <summary><see cref="Environment.TickCount64"/> 座標系的下次續約時刻。</summary>
    private long _nextRenewAt;

    /// <summary>押到這個時刻為止；過了而且路徑沒在跑就交回租約。</summary>
    private long _leaseWantedUntil;

    /// <summary>確認過沒有租約端點之後，下次重新探測的時刻。</summary>
    private long _nextProbeAt;

    /// <summary>提供端沒有租約端點（vnavmesh 沒裝，或版本早於 7.20.0.37）。</summary>
    private bool _leaseUnsupported;

    /// <summary>
    /// 每幀呼叫（<c>DalamudInitializer.FrameworkUpdate</c> 的第一行）：續約，以及在這段移動
    /// 結束之後把租約交回去。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>沒有租約時整支只是一次欄位比較，完全不碰 IPC。</b>
    /// 🔴 Questionable 一趟任務可以跑好幾個小時，而租期上限是 5 分鐘 ⇒ 續約非跑到不可。
    /// </remarks>
    public void UpdateLease()
    {
        Guid lease;
        bool renewDue;
        lock (_leaseGate)
        {
            lease = _lease;
            if (lease == Guid.Empty)
                return;
            renewDue = Environment.TickCount64 >= _nextRenewAt;
        }

        // 🔴 IsPathRunning 會走 IPC ⇒ 在鎖外呼叫。
        bool running = IsPathRunning;
        long now = Environment.TickCount64;
        bool idle;
        lock (_leaseGate)
        {
            if (running)
                _leaseWantedUntil = now + LeaseIdleGraceMilliseconds;
            idle = now >= _leaseWantedUntil;
        }

        if (idle)
        {
            ReleaseLease("這段移動已結束");
            return;
        }

        if (!renewDue)
            return;

        lock (_leaseGate)
            _nextRenewAt = now + RenewIntervalMilliseconds;

        bool renewed = IpcInvoke.SafeFunc(() => _renewSuppressionFor.InvokeFunc(lease, LeaseMilliseconds), false,
            _logger, "續約 vnavmesh 移動租約 {Lease} 時發生例外", lease);
        if (renewed)
            return;

        // 🔴 回 false ＝那把已經不在了（逾時，或 vnavmesh 被重載）。不能當成續約成功繼續跑；
        //    而且這段移動可能還要跑好幾分鐘，等下一次 Pathfind() 太晚 ⇒ 當幀就重新取得一把。
        _logger.LogInformation("vnavmesh 移動租約 {Lease} 已經不在了，重新取得一把", lease);
        lock (_leaseGate)
        {
            if (_lease == lease)
                _lease = Guid.Empty;
        }

        EnsureLease();
    }

    /// <summary>
    /// 宣告「接下來這段時間 Questionable 要用自己的路徑容許值」，需要的話順便取得租約。
    /// </summary>
    /// <returns>
    /// <see langword="false"/>＝租約這條路走不通（vnavmesh 沒裝，或版本早於 7.20.0.37），
    /// 由呼叫端決定要不要退回既有的全域寫入。
    /// </returns>
    private bool TouchLease()
    {
        long now = Environment.TickCount64;
        lock (_leaseGate)
        {
            _leaseWantedUntil = now + LeaseIdleGraceMilliseconds;
            if (_lease != Guid.Empty)
                return true;
            if (_leaseUnsupported && now < _nextProbeAt)
                return false;
        }

        return EnsureLease();
    }

    /// <summary>取得一把租約並把容許值押上去。</summary>
    private bool EnsureLease()
    {
        Guid lease = IpcInvoke.SafeFunc(() => _acquireSuppressionFor.InvokeFunc(LeaseOwner, LeaseMilliseconds),
            Guid.Empty, _logger, "向 vnavmesh 取得移動租約時發生例外");

        if (lease == Guid.Empty)
        {
            bool first;
            lock (_leaseGate)
            {
                first = !_leaseUnsupported;
                _leaseUnsupported = true;
                _nextProbeAt = Environment.TickCount64 + LeaseProbeIntervalMilliseconds;
            }

            if (first)
                _logger.LogInformation(
                    "vnavmesh 沒有移動租約端點（沒裝，或版本早於 7.20.0.37），退回既有的 Path.SetTolerance 全域寫入");
            return false;
        }

        // 🔴 新租約的兩個受控值都是 null（＝對什麼都沒有意見）—— 光是持有它不會改變任何行為，
        //    要另外把容許值押上去才會生效。
        bool applied = IpcInvoke.SafeFunc(() => _setLeasedTolerance.InvokeFunc(lease, PathTolerance), false,
            _logger, "設定 vnavmesh 租約 {Lease} 的路徑容許值時發生例外", lease);

        if (!applied)
        {
            // 那把在兩次呼叫之間就沒了。交回去，這一次退回全域寫入。
            _logger.LogInformation("vnavmesh 租約 {Lease} 無法設定路徑容許值，已交回", lease);
            IpcInvoke.SafeFunc(() => _releaseSuppression.InvokeFunc(lease), false);
            return false;
        }

        long now = Environment.TickCount64;
        lock (_leaseGate)
        {
            _lease = lease;
            _leaseUnsupported = false;
            _nextRenewAt = now + RenewIntervalMilliseconds;
            if (_leaseWantedUntil < now)
                _leaseWantedUntil = now + LeaseIdleGraceMilliseconds;
        }

        _logger.LogInformation("已向 vnavmesh 取得移動租約 {Lease}，路徑容許值 {Tolerance}（{Milliseconds} 毫秒）",
            lease, PathTolerance, LeaseMilliseconds);
        return true;
    }

    /// <summary>交回租約（沒有就什麼都不做）。冪等。</summary>
    private void ReleaseLease(string reason)
    {
        Guid lease;
        lock (_leaseGate)
        {
            lease = _lease;
            if (lease == Guid.Empty)
                return;

            // 🔴 先清掉自己的欄位再送出：送出途中擲例外的話，我們手上這把也已經是廢的了，
            //    留著只會讓下一幀誤以為還押著。
            _lease = Guid.Empty;
        }

        IpcInvoke.SafeFunc(() => _releaseSuppression.InvokeFunc(lease), false, _logger,
            "交回 vnavmesh 移動租約 {Lease} 時發生例外（那把會自行逾時）", lease);
        _logger.LogInformation("已交回 vnavmesh 移動租約 {Lease}：{Reason}", lease, reason);
    }

    /// <summary>卸載時把租約交回去。</summary>
    /// <remarks>
    /// 🔴 不交也只是等它逾時（最多 5 分鐘，提供端會自動放開並寫一行 log），但那段期間
    /// vnavmesh 的路徑容許值還押在我們的值上。
    /// 🔴 <b>這支絕對不能擲例外</b>：<c>ServiceProvider.Dispose()</c> 不吞例外，一個服務炸掉
    /// 會讓排在後面的所有服務的 <c>Dispose()</c> 都不執行（見 QuestionablePlugin.Dispose 的註解）。
    /// </remarks>
    public void Dispose()
    {
        try
        {
            ReleaseLease("Questionable 正在卸載");
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, "交回 vnavmesh 移動租約時發生非 IPC 例外，已忽略（那把會自行逾時）");
        }
    }
}
