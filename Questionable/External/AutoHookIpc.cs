#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
using System;
using ECommons.EzIpcManager;
using Microsoft.Extensions.Logging;
namespace Questionable.External;

internal sealed class AutoHookIpc : IAutoHookIpc
{
    private readonly ILogger<AutoHookIpc> _logger;

    [EzIPC("GetPluginState")] private readonly Func<bool> _isPluginEnabled;
    [EzIPC("SetPluginState")] private readonly Action<bool> _setPluginEnabled;
    [EzIPC("GetAutoStartFishing")] private readonly Func<bool> _getAutoStartFishing;
    [EzIPC("SetAutoStartFishing")] private readonly Action<bool> _setAutoStartFishing;
    [EzIPC("SetAutoGigState")] private readonly Action<bool> _setAutoGigState;
    [EzIPC("SetPreset")] private readonly Action<string> _setPreset;
    [EzIPC("SetPresetAutogig")] private readonly Action<string> _setPresetAutogig;
    [EzIPC("CreateAndSelectAnonymousPreset")] private readonly Action<string> _createAndSelectAnonymousPreset;
    [EzIPC("ImportAndSelectPreset")] private readonly Action<string> _importAndSelectPreset;
    [EzIPC("DeleteSelectedPreset")] private readonly Action _deleteSelectedPreset;
    [EzIPC("DeleteAllAnonymousPresets")] private readonly Action _deleteAllAnonymousPresets;
    /// <returns>
    /// If bait was swapped successfully or already equipped
    /// </returns>
    [EzIPC("SwapBaitById")] private readonly Func<uint, bool> _swapBaitById;
    /// <returns>
    /// If bait was swapped successfully or already equipped
    /// </returns>
    [EzIPC("SwapBait")] private readonly Func<string, bool> _swapBait;
    /// <summary>
    /// Swaps the current swimbait slot by index (0,1,2).
    /// </summary>
    /// <returns>
    /// If bait was swapped successfully or already equipped
    /// </returns>
    [EzIPC("SwapSwimbaitByIndex")] private readonly Func<byte, bool> _swapSwimbaitByIndex;

    public AutoHookIpc(ILogger<AutoHookIpc> logger)
    {
        _logger = logger;
        // 🔴 這裡刻意不帶 SafeWrapper。SafeWrapperIPC 的 InvokeFunction 攔下 IpcNotReadyError、
        // 呼叫 EzIPC.OnSafeInvocationException 之後 return default，**不重擲**——於是:
        //   ① IsAvailable() 的實作是「呼叫探測、沒擲例外就 return true」⇒ 恆為 true，
        //      AutoHook 不在時照樣產生釣魚任務，卡在等一個沒有接收者的 /ahstart。
        //   ② IsPluginEnabled() 恆為 false ⇒ DoFish 的 _wasAutoHookEnabled 恆為 false ⇒
        //      Cleanup() 會把使用者原本開著的 AutoHook 關掉。
        // 本檔的 IpcInvoke.SafeFunc 已經分級處理好了(IpcNotReadyError 靜默早退，
        // 其餘 IpcError／TargetInvocationException 才 LogWarning)，只是被上一層吃掉走不到。
        // ⚠️ 代價:這條通道的失敗不再經過 EzIpcFailureLog(它掛在 OnSafeInvocationException 上)，
        //    改由 IpcInvoke 自己記錄。
        EzIPC.Init(this, "AutoHook");
    }

    public bool IsAvailable() =>
        IpcInvoke.SafeFunc(() =>
        {
            // Probe IPC only: IsPluginEnabled() would return false for an installed-but-disabled plugin,
            // which DoFish handles by enabling AutoHook. We only need to know whether IPC succeeded.
            _isPluginEnabled();
            return true;
        }, fallback: false, _logger, "AutoHook plugin is not available");

    public bool IsPluginEnabled() =>
        IpcInvoke.SafeFunc(() => _isPluginEnabled(), fallback: false, _logger, "Unable to get AutoHook plugin state");

    public bool SetPluginEnabled(bool enabled)
    {
        _logger.LogInformation("Setting AutoHook plugin state to {Enabled}", enabled);
        return IpcInvoke.SafeFunc(() =>
        {
            _setPluginEnabled(enabled);
            return true;
        }, fallback: false, _logger, "Unable to set AutoHook plugin state");
    }

    public bool GetAutoStartFishing() =>
        IpcInvoke.SafeFunc(() => _getAutoStartFishing(), fallback: false, _logger,
            "Unable to get AutoHook auto-start fishing state");

    public bool SetAutoStartFishing(bool enabled)
    {
        _logger.LogInformation("Setting AutoHook auto-start fishing to {Enabled}", enabled);
        return IpcInvoke.SafeFunc(() =>
        {
            _setAutoStartFishing(enabled);
            return true;
        }, fallback: false, _logger, "Unable to set AutoHook auto-start fishing state");
    }

    public bool SetAutoGigState(bool enabled)
    {
        _logger.LogInformation("Setting AutoHook auto-gig state to {Enabled}", enabled);
        return IpcInvoke.SafeFunc(() =>
        {
            _setAutoGigState(enabled);
            return true;
        }, fallback: false, _logger, "Unable to set AutoHook auto-gig state");
    }

    public bool SetPreset(string presetName)
    {
        _logger.LogInformation("Setting AutoHook preset to {Preset}", presetName);
        return IpcInvoke.SafeFunc(() =>
        {
            _setPreset(presetName);
            return true;
        }, fallback: false, _logger, "Unable to set AutoHook preset");
    }

    public bool SetPresetAutogig(string presetName)
    {
        _logger.LogInformation("Setting AutoHook autogig preset to {Preset}", presetName);
        return IpcInvoke.SafeFunc(() =>
        {
            _setPresetAutogig(presetName);
            return true;
        }, fallback: false, _logger, "Unable to set AutoHook autogig preset");
    }

    public bool CreateAndSelectAnonymousPreset(string compressedPresetJson)
    {
        _logger.LogInformation("Creating and selecting anonymous AutoHook preset");
        return IpcInvoke.SafeFunc(() =>
        {
            _createAndSelectAnonymousPreset(compressedPresetJson);
            return true;
        }, fallback: false, _logger, "Unable to create and select anonymous AutoHook preset");
    }

    public bool ImportAndSelectPreset(string compressedPresetJson)
    {
        _logger.LogInformation("Importing and selecting AutoHook preset");
        return IpcInvoke.SafeFunc(() =>
        {
            _importAndSelectPreset(compressedPresetJson);
            return true;
        }, fallback: false, _logger, "Unable to import and select AutoHook preset");
    }

    public bool DeleteSelectedPreset()
    {
        _logger.LogInformation("Deleting selected AutoHook preset");
        return IpcInvoke.SafeFunc(() =>
        {
            _deleteSelectedPreset();
            return true;
        }, fallback: false, _logger, "Unable to delete selected AutoHook preset");
    }

    public bool DeleteAllAnonymousPresets()
    {
        _logger.LogInformation("Deleting all anonymous AutoHook presets");
        return IpcInvoke.SafeFunc(() =>
        {
            _deleteAllAnonymousPresets();
            return true;
        }, fallback: false, _logger, "Unable to delete anonymous AutoHook presets");
    }

    public bool SwapBaitById(uint baitId)
    {
        _logger.LogInformation("Swapping AutoHook bait by id {BaitId}", baitId);
        return IpcInvoke.SafeFunc(() => _swapBaitById(baitId), fallback: false, _logger, "Unable to swap AutoHook bait by id");
    }

    public bool SwapBait(string baitNameOrId)
    {
        _logger.LogInformation("Swapping AutoHook bait {Bait}", baitNameOrId);
        return IpcInvoke.SafeFunc(() => _swapBait(baitNameOrId), fallback: false, _logger, "Unable to swap AutoHook bait");
    }

    public bool SwapSwimbaitByIndex(byte index)
    {
        _logger.LogInformation("Swapping AutoHook swimbait slot {Index}", index);
        return IpcInvoke.SafeFunc(() => _swapSwimbaitByIndex(index), fallback: false, _logger,
            "Unable to swap AutoHook swimbait");
    }
}
