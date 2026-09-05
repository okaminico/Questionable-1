using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.Reflection;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Data;
using System;
namespace Questionable.External;

/// <summary>
/// 在 Questionable 跑自動化的期間請 YesAlready 讓開。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>改用具名租約的理由＝舊的開關沒有主人。</b>
/// 舊做法是直接寫 YesAlready 的 <c>SetPluginEnabled</c>（單一格全域布林），並自己留一份
/// <c>_wasEnabled</c> 快照來還原。問題是 SomethingNeedDoing／AutoDuty 也寫同一格：
/// 跑任務途中巨集碰一下那個開關，這裡的守衛條件 <c>IsPluginEnabled() &amp;&amp; !_wasEnabled</c>
/// 就再也不成立 ⇒ 這趟任務剩下的時間 YesAlready 一直開著搶按窗；反向則是任務跑完後
/// 被永久關掉，使用者以為外掛壞了。<b>兩種都全程零訊息。</b>
/// <para>
/// 🔑 租約端點（<c>AcquireSuppressionFor</c>／<c>RenewSuppressionFor</c>／<c>ReleaseSuppression</c>）
/// 是<b>記名</b>的 refcount：我們只放開自己那一把，不會影響別人，也完全不碰使用者的開關。
/// </para>
/// <para>
/// 🔴 <b>租約會逾時</b>（提供端上限 5 分鐘），而 Questionable 一趟可以跑好幾個小時
/// ⇒ 必須定期續約，並且<b>把續約的回傳值當真</b>：回 <see langword="false"/> 代表那把已經
/// 不在了（逾時、或使用者按了 YesAlready 的「強制解除鎖定」），要<b>重新取得</b>，
/// 不能繼續假設自己還壓著。
/// </para>
/// <para>
/// 🔴 <b>fail-safe</b>：使用者不一定裝 YesAlready，裝了也不一定是有租約端點的版本。
/// 取租約拿到 <see cref="Guid.Empty"/>（<see cref="SafeWrapper.IPCException"/> 把
/// <c>IpcNotReadyError</c> 吃掉後的預設值）就<b>退回改動前的舊路徑</b>，逐字沿用
/// <c>_wasEnabled</c> 那套。絕不因為提供端缺席就卡住任務流程。
/// </para>
/// </remarks>
internal sealed class YesAlreadyIpc : IDisposable
{
    /// <summary>租約登記的名字，會出現在 YesAlready 的 log 與設定視窗。</summary>
    private const string LeaseOwner = "Questionable";

    /// <summary>每次取得／續約時要求的租期（5 分鐘）＝提供端的硬性上限。</summary>
    /// <remarks>
    /// 🔑 全艦隊的壓制租約時間政策統一成「租 5 分鐘、每 30 秒續約」（AutoRetainer 那套
    /// 本來就是這個值）。取捨是：租期短 ⇒ 我們當掉或被卸載時，使用者最多等 5 分鐘
    /// YesAlready 就自己恢復；續約間隔留 10 倍餘裕 ⇒ 要連續漏掉 9 次心跳才會真的過期。
    /// <para>
    /// 🔴 這個值<b>不可以</b>大於提供端的上限：提供端是<b>夾值不是拒絕</b>，要多了只會
    /// 被靜默砍短，續約反而會來不及。
    /// </para>
    /// </remarks>
    private const int LeaseMilliseconds = 300_000;

    /// <summary>續約間隔（30 秒），是 <see cref="LeaseMilliseconds"/> 的十分之一。</summary>
    private const int RenewIntervalMilliseconds = 30_000;

    private static readonly EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(YesAlreadyIpc), "YesAlready", SafeWrapper.IPCException);
    [EzIPC("IsPluginEnabled")] public static readonly Func<bool> IsPluginEnabled;
    [EzIPC("SetPluginEnabled")] private static readonly Action<bool> SetPluginEnabled;

    // 租約端點。提供端沒有這幾支時（舊版 YesAlready），SafeWrapper.IPCException 會把
    // IpcNotReadyError 吃掉並回 default ⇒ Guid.Empty／false，正好就是我們的 fail-safe 訊號。
    [EzIPC("AcquireSuppressionFor")] private static readonly Func<string, int, Guid> AcquireSuppressionFor;
    [EzIPC("RenewSuppressionFor")] private static readonly Func<Guid, int, bool> RenewSuppressionFor;
    [EzIPC("ReleaseSuppression")] private static readonly Func<Guid, bool> ReleaseSuppression;

    private readonly IClientState _clientState;

    private readonly IFramework _framework;
    private readonly ILogger<YesAlreadyIpc> _logger;
    private readonly QuestController _questController;
    private readonly TerritoryData _territoryData;

    private bool _wasEnabled;

    /// <summary>目前持有的租約；<see cref="Guid.Empty"/>＝沒有。</summary>
    private Guid _lease;

    /// <summary><see cref="Environment.TickCount64"/> 座標系的下次續約時刻。</summary>
    private long _nextRenewAt;

    /// <summary>
    /// 提供端到底有沒有租約端點：<see langword="null"/>＝還沒試過、
    /// <see langword="true"/>＝有、<see langword="false"/>＝沒有（走舊路徑）。
    /// </summary>
    /// <remarks>
    /// 🔴 探測結果要記住，否則每一幀都會對一個不存在的端點發一次 IPC。
    /// YesAlready 重載時（<see cref="_yesAlreadyWasReady"/> 由 <see langword="false"/> 轉回
    /// <see langword="true"/>）會重設成 <see langword="null"/> 重新探測 —— 使用者中途更新
    /// YesAlready 就不必重開遊戲。
    /// </remarks>
    private bool? _leaseSupported;

    /// <summary>上一幀 YesAlready 在不在，用來偵測它被重載。</summary>
    private bool _yesAlreadyWasReady;

    public YesAlreadyIpc(IFramework framework,
        QuestController questController,
        TerritoryData territoryData,
        IClientState clientState,
        ILogger<YesAlreadyIpc> logger)
    {
        _framework = framework;
        _questController = questController;
        _territoryData = territoryData;
        _clientState = clientState;
        _logger = logger;
        _wasEnabled = IsPluginEnabled();
        _logger.LogInformation($"Enabled:{_wasEnabled}");

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;

        // 卸載時把自己那把租約交回去。不交也只是等它逾時（提供端會自動放開並寫一行 log），
        // 但那段期間 YesAlready 是啞的，所以還是明確放開。
        ReleaseLease("Questionable 正在卸載");

        IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    private void OnUpdate(IFramework framework)
    {
        bool ready = IPCSubscriber_Common.IsReady("YesAlready");
        if (ready != _yesAlreadyWasReady)
        {
            _yesAlreadyWasReady = ready;

            // YesAlready 被重載過：它那邊的租約表整個沒了，我們手上的 Guid 是廢的。
            // 能力探測也要重來（使用者可能剛把它更新到有租約端點的版本）。
            _lease = Guid.Empty;
            _leaseSupported = null;
        }

        if (!ready)
            return;

        bool hasActiveQuest = (_questController.IsRunning ||
                               _questController.AutomationType != QuestController.EAutomationType.Manual) &&
                              !_territoryData.IsDutyInstance(_clientState.TerritoryType);

        if (_leaseSupported != false)
        {
            if (hasActiveQuest)
            {
                AcquireOrRenewLease();

                // 這一幀才發現提供端沒有租約端點 ⇒ 當幀直接掉到下面的舊路徑，不要空等一幀。
                if (_leaseSupported != false)
                    return;
            }
            else
            {
                ReleaseLease("Questionable 的自動化已結束");
                return;
            }
        }

        // ── fail-safe：提供端沒有租約端點時，以下與改動前逐字相同 ──
        // ⚠️ 這段（含 _wasEnabled 在建構式被初始化成 IsPluginEnabled() 的既有行為）刻意原樣保留。
        if (hasActiveQuest)
        {
            if (IsPluginEnabled() && !_wasEnabled)
            {
                _logger.LogDebug("Requested YesAlready off");
                SetPluginEnabled(false);
                _wasEnabled = true;
            }
        }
        else
        {
            if (!IsPluginEnabled() && _wasEnabled)
            {
                _logger.LogDebug("Requested YesAlready on");
                SetPluginEnabled(true);
                _wasEnabled = false;
            }
        }
    }

    /// <summary>已經有租約就（節流地）續約，沒有就取一把新的。</summary>
    private void AcquireOrRenewLease()
    {
        if (_lease != Guid.Empty)
        {
            if (Environment.TickCount64 < _nextRenewAt)
                return;

            _nextRenewAt = Environment.TickCount64 + RenewIntervalMilliseconds;

            bool renewed;
            try
            {
                renewed = RenewSuppressionFor(_lease, LeaseMilliseconds);
            }
            catch (Exception e)
            {
                // SafeWrapper 只吃 IpcNotReadyError；型別不符之類的會漏出來，
                // 不能讓它打斷整個 Framework.Update。
                _logger.LogInformation(e, "續約 YesAlready 壓制租約時發生例外，改為重新取得一把");
                renewed = false;
            }

            if (renewed)
                return;

            // 🔴 回 false ＝那把已經不在了（逾時、或使用者按了「強制解除鎖定」）。
            // 不能當成續約成功繼續跑，必須重新取得。
            _logger.LogInformation("YesAlready 壓制租約 {Lease} 已經不在了，重新取得一把", _lease);
            _lease = Guid.Empty;
        }

        Guid lease;
        try
        {
            lease = AcquireSuppressionFor(LeaseOwner, LeaseMilliseconds);
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, "向 YesAlready 取得壓制租約時發生例外，退回舊的開關寫入");
            lease = Guid.Empty;
        }

        if (lease == Guid.Empty)
        {
            // 提供端沒裝、或版本太舊沒有這支端點。退回舊路徑，不要卡住任務流程。
            if (_leaseSupported != false)
                _logger.LogInformation("YesAlready 沒有壓制租約端點（版本太舊？），退回舊的開關寫入");

            _leaseSupported = false;
            return;
        }

        _leaseSupported = true;
        _lease = lease;
        _nextRenewAt = Environment.TickCount64 + RenewIntervalMilliseconds;
        _logger.LogInformation("已向 YesAlready 取得壓制租約 {Lease}（{Milliseconds} 毫秒）", lease, LeaseMilliseconds);
    }

    /// <summary>交回租約（沒有就什麼都不做）。冪等。</summary>
    private void ReleaseLease(string reason)
    {
        if (_lease == Guid.Empty)
            return;

        Guid lease = _lease;

        // 🔴 先清掉自己的欄位再送出：送出途中擲例外的話，我們手上這把也已經是廢的了，
        // 留著只會讓下一幀誤以為還壓著。
        _lease = Guid.Empty;

        try
        {
            ReleaseSuppression(lease);
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, "交回 YesAlready 壓制租約時發生例外（租約會自行逾時）");
        }

        _logger.LogInformation("已交回 YesAlready 壓制租約 {Lease}：{Reason}", lease, reason);
    }

    internal sealed class IPCSubscriber_Common
    {
        internal static bool IsReady(string pluginName)
        {
            return DalamudReflector.TryGetDalamudPlugin(pluginName, out IDalamudPlugin? _, false, true);
        }

        internal static Version? Version(string pluginName)
        {
            Version _version;
            if (DalamudReflector.TryGetDalamudPlugin(pluginName, out IDalamudPlugin? dalamudPlugin, false, true))
            {
                _version = dalamudPlugin.GetType().Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            }
            else
            {
                _version = new(0, 0, 0, 0);
            }
            return _version;
        }

        internal static void DisposeAll(EzIPCDisposalToken[] _disposalTokens)
        {
            foreach(EzIPCDisposalToken token in _disposalTokens)
            {
                try
                {
                    token.Dispose();
                }
                catch(Exception ex)
                {
                    Svc.Log.Error($"Error while unregistering IPC: {ex}");
                }
            }
        }
    }
}
