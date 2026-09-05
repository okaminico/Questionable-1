using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Questionable.Utils;

/// <summary>
///     「同一扇視窗的同一個按法，按過就不要再按，直到它真的收掉」的共用閘門。
///     Questionable 對 addon 的所有按法（<c>FireCallbackInt</c>、<c>FireCallback</c>、
///     <c>Callback.Fire</c>、<c>Close(true)</c>、<c>AddonMaster</c> 的按鈕點擊）都要先問過
///     <see cref="TryBeginPress(string, AtkUnitBase*, string, int)" />。
/// </summary>
/// <remarks>
///     <para>
///         🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>：<c>SelectYesno</c> 這類「按下即關」的窗被按下之後
///         有<b>「正在關閉中」的幾幀</b>，這段期間 <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
///         <c>UldManager.LoadedState == Loaded</c> 也都還成立（＝ ECommons <c>IsAddonReady</c> 三關全過、
///         擋不住這個窗口；本 repo 的 <see cref="AddonUtils.IsAddonReady" /> 更寬鬆）。
///         此時再對它送 callback／輸入事件就是原生 AccessViolation（C0000005）。AVE 在 .NET Core 是
///         corrupted-state exception，<c>try</c>/<c>catch</c> 完全攔不到，遊戲當場關閉 ——
///         <b>唯一的防護是「不要送第二次」，不是「送了再接住」</b>。
///     </para>
///     <para>
///         🔴 節流<b>不是</b>防護：<c>EzThrottler.Throttle("Confirm", 2000)</c>（<c>CommenceExecutor</c>）記的是
///         「上一次動作在哪個時刻」而不是「這扇窗已經按過」，而且 key 是全外掛共用、<b>首次必放行</b>；
///         <c>PurchaseState.NextStep</c>／<c>_remainingAllowances</c>／<c>PointMenuCounter</c> 記的是
///         「流程走到哪」而不是「這個實例按過沒」。它們全都無法區分「同一扇正在關閉的窗」與「新的一扇窗」。
///     </para>
///     <para>
///         🔑 <b>粒度＝(窗名, 位址, 參數組)</b>。同幀對同一扇窗連送<b>不同</b>參數是既有的正常流程
///         （<c>MultipleHelpWindow</c> 的 −2 後接 −1、<c>ContextIconMenu</c> 的挑選後接關閉、
///         <c>Gathering</c> 逐格挑採集品），照抄「一扇窗只按一次」會把它們弄壞。
///         只有<b>「回答一次即終結」</b>的窗（<see cref="SingleAnswerAddons" />）才把所有參數併成同一個 key。
///     </para>
///     <para>
///         🔴 <b>時鐘不能用 <c>UiBuilder.FrameCount</c></b>：那個計數器的遞增點在 <c>OnDraw()</c> 的三個
///         「隱藏 UI 就 return」之後（過場動畫、使用者按隱藏 UI 熱鍵、GPose，三個開關預設全開），
///         過場中它<b>完全不前進</b> —— 而按下點走的是 AddonLifecycle（原生 detour）照常每幀被叫到，
///         結果會變成「按下照常、逃生口永不到期」。本類別自己在 <see cref="IFramework.Update" /> 裡數，
///         那條路徑與 UI 隱藏無關。
///     </para>
///     <para>
///         🔴 <b>解除一律按位址，不按名稱</b>：同名的第二扇窗被建立時若整包清掉 addonName 的紀錄，
///         第一扇（正在關閉中）的紀錄會跟著消失 ⇒ 下一幀對它再送一發＝崩潰。
///     </para>
///     <para>
///         被擋下時一律回 <see langword="false" />（不是 <see langword="null" />），對呼叫端的意義是
///         「這一輪沒按到，下一輪再來」，與「addon 還沒出現」走同一條既有路徑，不改變任何控制流。
///     </para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>「按下即關」的窗用的逃生口：遠大於關閉所需的幀數（關閉中的危險窗口實測 &lt; 10 幀）。</summary>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    ///     「按一次翻一頁／挑一格，窗不會因為被按而消失」的多次互動窗用的逃生口。
    ///     15 幀不落在關閉中的危險窗口裡，而每次互動多 0.25 秒幾乎無感。
    ///     走這個逃生口是<b>常態</b>，所以寫 Debug 不洗版。
    /// </summary>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>掃全索引找「這扇窗還在不在」時的上界。多窗情境（同名多個實例）需要掃到第一個空的為止。</summary>
    private const int MaxAddonIndex = 32;

    /// <summary>PreFinalize 記號用的 key 前綴。呼叫端產生的 key 不會以此開頭。</summary>
    private const string DestroyingKeyPrefix = "~destroying:";

    /// <summary>
    ///     「回答一次即終結」的窗：任何一個答案按下去之後窗就會收掉，所以<b>所有</b>參數併成同一個 key。
    ///     ⚠️ 只放真的按下即關的；<c>SelectString</c>／<c>SelectIconString</c> 這種可能有次級選單的
    ///     不放進來（改用逐參數粒度），免得把巢狀選單擋成停擺。
    /// </summary>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "DifficultySelectYesNo",
        "HousingSelectBlock",
        "PointMenu",
    };

    private static readonly Dictionary<string, Dictionary<string, PressRecord>> PressedByAddon =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> SetupWatchers =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> FinalizeWatchers =
        new(StringComparer.Ordinal);

    // 每幀輪詢用的可重用緩衝，沒有窗被記著時整個輪詢是一個整數比較就回來，不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<string> KeysBuf = [];
    private static readonly List<string> ForgetBuf = [];

    private static long frameCount;

    /// <summary>0＝還沒訂閱。🔴 用 <see cref="Interlocked" /> 不用 <see cref="bool" />：重複訂閱不是沒效果，
    /// 是計數器一個 tick 前進 2 ＝所有逃生口對半砍，會把補按往危險窗口推。</summary>
    private static int clockStarted;

    private static long CurrentFrame => frameCount;

    /// <summary>
    ///     啟動時鐘與每幀輪詢。要在外掛建構子裡<b>盡早</b>呼叫（<c>ECommonsMain.Init</c> 之後）：
    ///     同一個外掛內部的 <see cref="IFramework.Update" /> 多播委派<b>包在單一 try/catch 裡</b>，
    ///     排在前面的處理常式擲例外會讓後面所有處理常式那個 tick 完全不被呼叫 ——
    ///     時鐘排越前面越不容易被別人的例外弄停。
    /// </summary>
    internal static void EnsureClock()
    {
        if (Svc.Framework == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref clockStarted, 1, 0) != 0)
        {
            return;
        }

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>拆掉監聽器。要在 <c>ECommonsMain.Dispose()</c> <b>之前</b>呼叫（之後 <c>Svc</c> 已經清空）。</summary>
    internal static void ForceTeardown()
    {
        if (Interlocked.Exchange(ref clockStarted, 0) == 1 && Svc.Framework != null)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
        }

        if (Svc.AddonLifecycle != null)
        {
            foreach ((string addonName, IAddonLifecycle.AddonEventDelegate handler) in SetupWatchers)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            }

            foreach ((string addonName, IAddonLifecycle.AddonEventDelegate handler) in FinalizeWatchers)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
            }
        }

        SetupWatchers.Clear();
        FinalizeWatchers.Clear();
        PressedByAddon.Clear();
    }

    /// <summary>
    ///     問「現在可以對這扇窗按下這一個按法嗎」，可以的話<b>順便把它登記起來</b>。
    ///     回 <see langword="false" /> 代表這一幀不要碰它。
    /// </summary>
    /// <param name="addonName">視窗名稱。要能被 <c>GetAddonByName</c> 找得到，解除才會生效。</param>
    /// <param name="addon">視窗實例。位址<b>只做等值比較，永遠不解參</b>。</param>
    /// <param name="pressKey">參數組。同一扇窗上不同的按法要用不同的 key。</param>
    /// <param name="escapeFrames">
    ///     逃生口：超過這麼多幀窗還在，就判定「上一次按下沒生效」而不是「正在關閉」，放行補按。
    ///     多次互動窗傳 <see cref="RoutineRePressEscapeFrames" />。
    /// </param>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "",
        int escapeFrames = DefaultEscapeFrames)
    {
        return TryBeginPress(addonName, (nint)addon, pressKey, escapeFrames);
    }

    /// <summary>
    ///     視窗名稱從實例自己讀（給只拿得到 addon id、拿不到名稱的呼叫端用，例如
    ///     <c>AgentSatisfactionSupply</c> / <c>AgentRecipeNote</c> 那兩條路徑）。
    /// </summary>
    /// <remarks>
    ///     🔴🔴 名稱一定要走 <see cref="AddonUtils.ReadAddonName" /> 這種<b>有界</b>讀法，
    ///     <b>不可以</b>用 CS 產生的 <c>NameString</c>（無上限的 null-terminated 掃描）：
    ///     這支守衛被呼叫的時機正好是「這扇窗可能正在關閉」，
    ///     在判定安全<b>之前</b>先對它做無界讀取，等於守衛自己去踩它要防的那顆雷。
    ///     除了偏移 0x8 那 32 個 byte 的固定欄位之外，位址一樣不解任何二級指標。
    /// </remarks>
    internal static bool TryBeginPress(AtkUnitBase* addon, string pressKey = "",
        int escapeFrames = DefaultEscapeFrames)
    {
        if (addon == null)
        {
            return false;
        }

        return TryBeginPress(AddonUtils.ReadAddonName(addon), (nint)addon, pressKey, escapeFrames);
    }

    /// <summary>
    ///     先問過守衛再送 <c>FireCallbackInt</c>。被擋下時這一幀什麼都不做，回 <see langword="false" />。
    ///     參數值本身就是 press key（＝(窗名, 位址, 參數組) 粒度）。
    /// </summary>
    internal static bool PressCallbackInt(string addonName, AtkUnitBase* addon, int value,
        int escapeFrames = DefaultEscapeFrames)
    {
        if (!TryBeginPress(addonName, addon, value.ToString(CultureInfo.InvariantCulture), escapeFrames))
        {
            return false;
        }

        addon->FireCallbackInt(value);
        return true;
    }

    /// <summary>
    ///     先問過守衛再呼叫 <c>Close(true)</c>。關閉也是一種「對正在關閉中的窗動手」，
    ///     所以與 callback 走同一個閘門。
    /// </summary>
    internal static bool PressClose(string addonName, AtkUnitBase* addon,
        int escapeFrames = DefaultEscapeFrames)
    {
        if (!TryBeginPress(addonName, addon, "Close", escapeFrames))
        {
            return false;
        }

        addon->Close(true);
        return true;
    }

    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "",
        int escapeFrames = DefaultEscapeFrames)
    {
        // 沒有窗就沒有按法 —— 呼叫端本來就會判空，這裡只是不讓 0 進到紀錄裡。
        if (address == 0)
        {
            return false;
        }

        // 🔑 追蹤不了的窗一律放行：守衛失效時要退回「既有行為」，不是把功能關掉。
        if (string.IsNullOrEmpty(addonName))
        {
            LogPressDiag(addonName, address, pressKey);
            return true;
        }

        // 🔴 時鐘沒起來就等於 frameCount 永遠是 0、逃生口永不到期、所有按下被擋死。
        // 這裡再保險一次（Interlocked 冪等），讓「建構子那次沒跑到」不會變成靜默鎖死。
        EnsureClock();

        bool singleAnswer = SingleAnswerAddons.Contains(addonName);
        if (singleAnswer)
        {
            pressKey = string.Empty;
        }

        EnsureWatching(addonName);

        long frame = CurrentFrame;
        bool routine = escapeFrames <= RoutineRePressEscapeFrames;

        if (!PressedByAddon.TryGetValue(addonName, out Dictionary<string, PressRecord>? presses))
        {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }
        else if (FindBlocking(presses, address, pressKey, singleAnswer, frame, out string blockingKey))
        {
            // 🔴 這就是崩潰的那一幀。
            LogHold(addonName, address, pressKey, blockingKey, routine);
            return false;
        }
        else if (presses.TryGetValue(pressKey, out PressRecord same) && same.Address == address)
        {
            LogRelease(addonName, address, pressKey, frame - same.Frame, routine);
        }

        presses[pressKey] = new PressRecord(address, frame, escapeFrames, false);
        LogPressDiag(addonName, address, pressKey);
        return true;
    }

    /// <summary>
    ///     每幀的時鐘＋輪詢解除。
    ///     🔴 <see cref="frameCount" /> 的遞增必須在所有 early return <b>之前</b> ——
    ///     放到後面的話，沒有窗被記著時時鐘就停住，逃生口的幀數會被算少，等於沒修。
    /// </summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;

        if (PressedByAddon.Count == 0)
        {
            return;
        }

        NamesBuf.Clear();
        foreach (string name in PressedByAddon.Keys)
        {
            NamesBuf.Add(name);
        }

        foreach (string name in NamesBuf)
        {
            if (!PressedByAddon.TryGetValue(name, out Dictionary<string, PressRecord>? presses))
            {
                continue;
            }

            PresentBuf.Clear();
            for (int i = 1; i <= MaxAddonIndex; i++)
            {
                nint live = (nint)Svc.GameGui.GetAddonByName<AtkUnitBase>(name, i);
                if (live == 0)
                {
                    break;
                }

                PresentBuf.Add(live);
            }

            KeysBuf.Clear();
            foreach ((string key, PressRecord rec) in presses)
            {
                if (!PresentBuf.Contains(rec.Address))
                {
                    KeysBuf.Add(key);
                }
            }

            foreach (string key in KeysBuf)
            {
                presses.Remove(key);
            }

            if (presses.Count == 0)
            {
                PressedByAddon.Remove(name);
            }
        }
    }

    private static bool FindBlocking(Dictionary<string, PressRecord> presses, nint address, string pressKey,
        bool singleAnswer, long frame, out string blockingKey)
    {
        foreach ((string key, PressRecord rec) in presses)
        {
            if (rec.Address != address)
            {
                continue;
            }

            if (frame - rec.Frame >= rec.EscapeFrames)
            {
                // 冷了：交給逃生口放行，免得呼叫端卡到逾時。
                continue;
            }

            if (!rec.BlocksAll && !singleAnswer && !string.Equals(key, pressKey, StringComparison.Ordinal))
            {
                continue;
            }

            blockingKey = key;
            return true;
        }

        blockingKey = string.Empty;
        return false;
    }

    /// <summary>
    ///     為某個視窗名稱掛上生命週期監聽器。
    ///     🔴 兩個 handler 都<b>按位址</b>處理：PostSetup 只清「這個位址」的紀錄，PreFinalize 只鎖「這個位址」。
    ///     丟掉 <c>args</c> 改用名稱整包清，會在「同名第二扇窗開起來」時把第一扇（正在關閉中）的紀錄一起清掉。
    /// </summary>
    private static void EnsureWatching(string addonName)
    {
        if (SetupWatchers.ContainsKey(addonName))
        {
            return;
        }

        IAddonLifecycle.AddonEventDelegate onSetup = (_, args) =>
        {
            nint address = args.Addon.Address;
            if (address != 0)
            {
                ForgetAddress(addonName, address);
            }
        };

        IAddonLifecycle.AddonEventDelegate onFinalize = (_, args) =>
        {
            nint address = args.Addon.Address;
            if (address != 0)
            {
                MarkDestroying(addonName, address);
            }
        };

        SetupWatchers[addonName] = onSetup;
        FinalizeWatchers[addonName] = onFinalize;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, onSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, onFinalize);
    }

    /// <summary>
    ///     這個位址上開了一扇新的窗：舊紀錄（含 PreFinalize 記號）作廢。
    /// </summary>
    /// <remarks>
    ///     🔴 <b>同幀豁免</b>：<b>這一幀才登記</b>的紀錄不清。
    ///     Questionable 有四個模組（<c>InteractionUiController</c>／<c>HelpUiController</c>／
    ///     <c>CreditsController</c>／<c>CraftworksSupplyController</c>）是<b>在 PostSetup handler 裡直接按下去</b>的，
    ///     而 <c>AddonLifecycle</c> 監聽器<b>彼此之間的呼叫順序不可依賴</b>
    ///     （服務端註冊走 <c>RunOnTick</c> 排進 <c>ConcurrentDictionary</c>，列舉順序與排入順序無關；
    ///     派送時直接 <c>foreach</c> 清單、不做快照）。
    ///     沒有這條豁免的話：模組先按下並登記 → 本守衛的 PostSetup 監聽器後跑 → 把剛登記的紀錄清掉
    ///     ⇒ 下一幀的 by-name 重查路徑再按一次就是原生存取違規，守衛等於沒接。
    ///     <para>
    ///         可以這樣寫是因為<b>同一遊戲幀內 <c>Framework.Update</c> 一律早於 addon 事件</b>，
    ///         所以按下與本監聽器讀到的 <see cref="CurrentFrame" /> 必定是同一個值。
    ///     </para>
    /// </remarks>
    private static void ForgetAddress(string addonName, nint address)
    {
        if (!PressedByAddon.TryGetValue(addonName, out Dictionary<string, PressRecord>? presses))
        {
            return;
        }

        long frame = CurrentFrame;
        ForgetBuf.Clear();
        foreach ((string key, PressRecord rec) in presses)
        {
            if (rec.Address == address && rec.Frame != frame)
            {
                ForgetBuf.Add(key);
            }
        }

        foreach (string key in ForgetBuf)
        {
            presses.Remove(key);
        }

        if (presses.Count == 0)
        {
            PressedByAddon.Remove(addonName);
        }
    }

    /// <summary>
    ///     這扇窗已經進入銷毀流程：在它真的消失（或同位址開出新窗）之前，<b>任何</b>按法都不准再碰它。
    ///     這條路徑罩的是「從 PreFinalize 事件裡回頭去按同一扇窗」的呼叫端
    ///     （<c>RegularShopBase.ShopPreFinalize</c> → <c>RestoreExternalPluginState</c> 就是一例）。
    /// </summary>
    private static void MarkDestroying(string addonName, nint address)
    {
        if (!PressedByAddon.TryGetValue(addonName, out Dictionary<string, PressRecord>? presses))
        {
            presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }

        presses[DestroyingKeyPrefix + address.ToString("X", CultureInfo.InvariantCulture)] =
            new PressRecord(address, CurrentFrame, DefaultEscapeFrames, true);
    }

    /// <summary>
    ///     跨外掛「按窗診斷」：在<b>真的送出按壓</b>的那一刻寫一行 <c>Information</c>。
    /// </summary>
    /// <remarks>
    ///     全艦隊 15 份各自獨立的 <c>AddonPressGuard</c> 只擋自己按過的位址：外掛 A 按下之後
    ///     「關閉中」那幾幀，外掛 B 的表是空的 ⇒ 照按 ⇒ 攔不到的存取違規。
    ///     這一行的用途是用一輪實機 log 回答「跨外掛重按是不是真的在發生」，
    ///     格式<b>逐字</b>與其他外掛統一，才能按 <c>addr</c> 交叉比對。
    ///     🔴 刻意<b>不節流</b>（漏掉一次就是漏掉一個對照樣本）；
    ///     🔴 位址只印數值，<b>不解參考</b>。
    ///     ⚠️ 追蹤不了窗名的那條放行路徑也記，只是 <c>addon=?</c>：那一次按壓照樣送得出去，
    ///     漏掉它會讓交叉比對出現看不見的缺口。
    /// </remarks>
    private static void LogPressDiag(string addonName, nint address, string pressKey)
    {
        string name = string.IsNullOrEmpty(addonName) ? "?" : addonName;
        Svc.Log.Information($"[按窗診斷] plugin=Questionable addon={name} addr=0x{address:X} key={pressKey ?? string.Empty}");
    }

    private static void LogHold(string addonName, nint address, string pressKey, string blockingKey, bool routine)
    {
        if (!EzThrottler.Throttle($"Questionable-AddonPressGuard-Hold-{addonName}", 1000))
        {
            return;
        }

        string msg =
            $"[AddonPressGuard] 「{addonName}」(實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}，按法「{pressKey}」)" +
            $"按過之後(紀錄「{blockingKey}」)還沒觀察到它收掉，這一幀不再碰它 —— " +
            "對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine)
        {
            Svc.Log.Debug(msg);
        }
        else
        {
            Svc.Log.Information(msg);
        }
    }

    private static void LogRelease(string addonName, nint address, string pressKey, long waited, bool routine)
    {
        if (routine)
        {
            if (EzThrottler.Throttle($"Questionable-AddonPressGuard-RoutineRelease-{addonName}", 10000))
            {
                Svc.Log.Debug(
                    $"[AddonPressGuard] 「{addonName}」(實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}，按法「{pressKey}」)" +
                    $"按下後 {waited} 幀窗還在(多次互動窗的常態)，放行下一次。");
            }
        }
        else if (EzThrottler.Throttle($"Questionable-AddonPressGuard-Release-{addonName}", 10000))
        {
            Svc.Log.Information(
                $"[AddonPressGuard] 「{addonName}」(實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}，按法「{pressKey}」)" +
                $"按下後 {waited} 幀既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」" +
                "而不是「正在關閉」，解除封鎖讓呼叫端重試。");
        }
    }

    /// <param name="Address">位址只做等值比較，永遠不解參。</param>
    /// <param name="BlocksAll">true＝這扇窗上的所有按法都被擋（PreFinalize 記號用）。</param>
    private readonly record struct PressRecord(nint Address, long Frame, int EscapeFrames, bool BlocksAll);
}
