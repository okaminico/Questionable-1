//Taken and adapted from https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/Movement/OverrideCamera.cs.

using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Microsoft.Extensions.Logging;
using System;
using System.Numerics;
namespace Questionable.Functions;

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose (same fix as vnavmesh/Lifestream on
// TC 7.20). Its 0x130-based FieldOffsets were the TC 7.15 layout — TC 7.20 shifted the native struct
// +0x10, so FFXIVClientStructs.FFXIV.Client.Game.Camera (verified against the API13 pin) now exposes
// exactly the fields we need; use it directly and let the pin track layout changes for us.

internal sealed unsafe class CameraFunctions : IDisposable
{
    private readonly ILogger<CameraFunctions> _logger;
    private readonly IObjectTable _objectTable;

    private readonly bool IgnoreUserInput = true; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    // The TC 7.15-era call-site signature (E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 44 0F 28 4C 24 ??)
    // scans zero hits on TC 7.20 (the call site's trailing movaps changed register allocation), which
    // silently disabled camera auto-facing. This is the function-prologue signature vnavmesh verified
    // on TC 7.20 (matches exactly once). Kept fallible, same as vnavmesh, so a future signature
    // drift only disables camera auto-facing instead of crashing the whole plugin.
    [Signature("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", Fallibility = Fallibility.Fallible)]
    private Hook<RMICameraDelegate>? _rmiCameraHook;
    private float DesiredAltitude;
    private float DesiredAzimuth;

    public CameraFunctions(IGameInteropProvider gameInteropProvider, ILogger<CameraFunctions> logger, IObjectTable objectTable)
    {
        _logger = logger;
        gameInteropProvider.InitializeFromAttributes(this);
        _objectTable = objectTable;
        if (_rmiCameraHook == null)
        {
            _logger.LogWarning("RMICamera signature not found - camera auto-facing disabled");
        }
    }

    public bool Enabled
    {
        get => _rmiCameraHook?.IsEnabled ?? false;
        set
        {
            if (_rmiCameraHook == null)
            {
                return;
            }

            if (value)
            {
                _rmiCameraHook.Enable();
            }
            else
            {
                _rmiCameraHook.Disable();
            }
        }
    }

    public void Dispose()
    {
        _rmiCameraHook?.Dispose();
    }

    private static float Deg2Rad(int degrees)
    {
        return degrees * ((float)Math.PI / 180f);
    }

    // from https://github.com/NightmareXIV/ECommons/blob/master/ECommons/MathHelpers/Angle.cs
    private static float Normalized(float r)
    {
        while(r < -MathF.PI)
        {
            r += 2 * MathF.PI;
        }
        while(r > MathF.PI)
        {
            r -= 2 * MathF.PI;
        }
        return r;
    }


    internal void Face(Vector3 pos)
    {
        _logger.LogDebug("Facing " + pos);
        Enabled = true;
        if (_objectTable[0] == null)
        {
            return;
        }
        Vector3 diff = pos - _objectTable[0]!.Position;
        DesiredAzimuth = MathF.Atan2(diff.X, diff.Z) + Deg2Rad(180);
        DesiredAltitude = Deg2Rad(-30);
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the game's own camera handling passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch (Framework.Instance() below is one).
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 1 - Debug is captured too, but drowned by the 100k+ Debug lines a single log file holds.
        DateTime now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastDetourErrorLog = now;
        _logger.LogInformation(ex,
            "Camera auto-facing threw, leaving the game's own camera input alone (total {Count})",
            _detourErrors);
    }

    private void RMICameraDetour(Camera* self, int inputMode, float speedH, float speedV)
    {
        _rmiCameraHook!.OriginalDisposeSafe(self, inputMode, speedH, speedV);
        try
        {
            if (self == null)
            {
                return;
            }

            if (IgnoreUserInput || inputMode == 0) // let user override...
            {
                // 🔴 上面那圈 try/catch 擋的是「特徵碼失配 -> ThrowNullAddress 丟
                //    InvalidOperationException」，但那不是這裡唯一的失敗形式。
                //    Framework.Instance() 是 [StaticAddress("48 8B 1D ?? ?? ?? ?? 8B 7C 24 64", 3,
                //    isPointer: true)]，產生器對 isPointer:true 產出的是
                //    「if (ppInstance is null) Throw...; return *ppInstance;」——
                //    判空判的是**外層指標槽的位址**，回傳的卻是**槽裡的內容**，
                //    而那個內容在遊戲還沒建好／正在拆掉 Framework 時合法為 null，從沒被判過。
                //    裸解參考它是 AccessViolationException（corrupted-state，catch 攔不到），
                //    而這個 detour 由原生程式碼直接呼叫，沒有第二道防線。
                // fail-closed：拿不到就當這一幀 dt = 0。Original() 已先跑過，
                //    遊戲自己的鏡頭處理原封不動。每幀都會跑，刻意不寫 log。
                // ⚠️ 注意 dt 在本方法目前沒有任何讀取端（maxH/maxV 是固定的 Deg2Rad(180)）——
                //    保留這個讀取以免回退既有行為，但它現在只是個安全的無用讀取。
                Framework* framework = Framework.Instance();
                float dt = framework == null ? 0f : framework->FrameDeltaTime;
                float deltaH = Normalized(DesiredAzimuth - self->DirH);
                float deltaV = Normalized(DesiredAltitude - self->DirV);
                float maxH = Deg2Rad(180);
                float maxV = Deg2Rad(180);
                self->InputDeltaH = Math.Clamp(deltaH, -maxH, maxH);
                self->InputDeltaV = Math.Clamp(deltaV, -maxV, maxV);
                Enabled = false;
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }

    private delegate void RMICameraDelegate(Camera* self, int inputMode, float speedH, float speedV);
}
