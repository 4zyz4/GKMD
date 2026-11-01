using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using GKMD;

namespace GKMD.Native;

// ════════════════════════════════════════════════════════════════════════
//  C ABI surface
//
//  The library is published with Native AOT (<NativeLib>Shared</NativeLib>),
//  so every entry point below is exported from the native binary:
//
//    Windows : GKMD.dll
//    Linux   : libGKMD.so
//
//  Rules that follow from Native AOT:
//    * Only [UnmanagedCallersOnly] methods are callable from outside .NET.
//    * Every parameter and return value is blittable; strings cross the
//      boundary as NUL-terminated UTF-8, and managed objects are hidden
//      behind opaque handles (void*).
//    * The managed API (classes, events, dictionaries) is intentionally
//      not exposed; see native/gkmd.h for the full contract.
//
//  Error convention: functions returning int return 0 on success and -1 on
//  failure; gkmd_last_error() then describes the failure on the calling
//  thread. Functions returning a handle return NULL on failure.
// ════════════════════════════════════════════════════════════════════════

/// <summary>One analog axis value. <see cref="Axis"/> is a HID usage
/// address (page &lt;&lt; 8 | usage), matching the managed GKAxis enum.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GkmdAxisValue
{
    public uint Axis;
    public float Value;
}

/// <summary>Blittable projection of GKGamepadState. Optional hat channels
/// use an explicit Has* flag because C has no nullable value types.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct GkmdGamepadState
{
    public GkmdAxisValue* Axes;
    public int AxisCount;

    public uint Buttons;
    public byte Hat;

    public byte HasHatDegrees;
    public float HatDegrees;
    public byte HasHatHundredths;
    public int HatHundredths;
    public byte HasHatRaw;
    public ushort HatRaw;

    public byte TouchpadFinger0Active;
    public ushort TouchpadFinger0X;
    public ushort TouchpadFinger0Y;
    public byte TouchpadFinger0Id;
    public byte TouchpadFinger1Active;
    public ushort TouchpadFinger1X;
    public ushort TouchpadFinger1Y;
    public byte TouchpadFinger1Id;
    public byte TouchpadPacketCounter;

    public short GyroPitch;
    public short GyroYaw;
    public short GyroRoll;
    public short AccelX;
    public short AccelY;
    public short AccelZ;
    public uint SensorTimestamp;

    public float AccelGX;
    public float AccelGY;
    public float AccelGZ;
    public float GyroDpsX;
    public float GyroDpsY;
    public float GyroDpsZ;

    public byte BatteryLevel;
    public byte BatteryCharging;
    public byte BatteryFull;
    public byte MicMuted;
    public byte HeadphonesConnected;
}

/// <summary>Callback invoked on the controller's output thread for every
/// rumble / haptic / FFB / LED packet. The data pointer is only valid for
/// the duration of the call.</summary>
public unsafe delegate void GkmdOutputCallback(
    uint source,
    byte reportId,
    byte* data,
    int length,
    uint seqNo,
    IntPtr userData);

public static unsafe class GKMDNative
{
    private const string Version = "4.9.0";

    [ThreadStatic] private static IntPtr _lastError;

    private static readonly IntPtr VersionUtf8 =
        Marshal.StringToCoTaskMemUTF8(Version);

    // ── Helpers ─────────────────────────────────────────────────────────

    private static void SetError(string message)
    {
        if (_lastError != IntPtr.Zero) Marshal.FreeCoTaskMem(_lastError);
        _lastError = Marshal.StringToCoTaskMemUTF8(message);
    }

    private static int Fail(Exception ex)
    {
        SetError(ex.ToString());
        return -1;
    }

    private static IntPtr ToHandle(object target)
        => GCHandle.ToIntPtr(GCHandle.Alloc(target));

    private static T? FromHandle<T>(IntPtr handle) where T : class
    {
        if (handle == IntPtr.Zero) return null;
        return GCHandle.FromIntPtr(handle).Target as T;
    }

    private static void FreeHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { GCHandle.FromIntPtr(handle).Free(); }
        catch { /* already freed or not a GCHandle */ }
    }

    /// <summary>Write a NUL-terminated UTF-8 string into a caller buffer.
    /// Returns the number of bytes written (excluding the terminator), or
    /// the required capacity when the buffer is too small. Callers can
    /// probe with buffer = NULL, capacity = 0.</summary>
    private static int WriteUtf8(string value, byte* buffer, int capacity)
    {
        int needed = Encoding.UTF8.GetByteCount(value);
        if (buffer == null || capacity <= needed) return needed;

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* src = bytes)
        {
            Buffer.MemoryCopy(src, buffer, capacity, bytes.Length);
        }
        buffer[bytes.Length] = 0;
        return bytes.Length;
    }

    private static string? ReadUtf8(IntPtr value)
        => value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);

    // ── Version / diagnostics ───────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "gkmd_version")]
    public static IntPtr VersionString() => VersionUtf8;

    [UnmanagedCallersOnly(EntryPoint = "gkmd_last_error")]
    public static IntPtr LastError() => _lastError;

    // ── Context ─────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_create")]
    public static IntPtr ContextCreate()
    {
        try
        {
            SetError(string.Empty);
            return ToHandle(new GKContext());
        }
        catch (Exception ex) { Fail(ex); return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_destroy")]
    public static void ContextDestroy(IntPtr context)
    {
        var ctx = FromHandle<GKContext>(context);
        FreeHandle(context);
        try { ctx?.Dispose(); }
        catch (Exception ex) { SetError(ex.ToString()); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_is_backend_available")]
    public static int ContextIsBackendAvailable()
    {
        try { return GKContext.IsUsbipBackendAvailable ? 1 : 0; }
        catch (Exception ex) { Fail(ex); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_install_backend")]
    public static int ContextInstallBackend()
    {
        try
        {
            GKContext.InstallUsbipBackend(null);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_load_default_profiles")]
    public static int ContextLoadDefaultProfiles(IntPtr context)
    {
        try
        {
            var ctx = FromHandle<GKContext>(context);
            if (ctx == null) { SetError("invalid context handle"); return -1; }
            return ctx.LoadDefaultProfiles();
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_profile_count")]
    public static int ContextProfileCount(IntPtr context)
    {
        try
        {
            var ctx = FromHandle<GKContext>(context);
            if (ctx == null) { SetError("invalid context handle"); return -1; }
            return ctx.AllProfiles.Count;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static GKProfile? ProfileAt(IntPtr context, int index)
    {
        var ctx = FromHandle<GKContext>(context);
        if (ctx == null) { SetError("invalid context handle"); return null; }
        var profiles = ctx.AllProfiles;
        if (index < 0 || index >= profiles.Count)
        {
            SetError($"profile index {index} is out of range [0, {profiles.Count})");
            return null;
        }
        return profiles[index];
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_profile_id")]
    public static int ContextProfileId(IntPtr context, int index, byte* buffer, int capacity)
    {
        try
        {
            var profile = ProfileAt(context, index);
            return profile == null ? -1 : WriteUtf8(profile.Id, buffer, capacity);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_profile_name")]
    public static int ContextProfileName(IntPtr context, int index, byte* buffer, int capacity)
    {
        try
        {
            var profile = ProfileAt(context, index);
            return profile == null ? -1 : WriteUtf8(profile.DisplayName, buffer, capacity);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_profile_vid")]
    public static int ContextProfileVid(IntPtr context, int index)
    {
        try
        {
            var profile = ProfileAt(context, index);
            return profile == null ? -1 : profile.VendorId;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_profile_pid")]
    public static int ContextProfilePid(IntPtr context, int index)
    {
        try
        {
            var profile = ProfileAt(context, index);
            return profile == null ? -1 : profile.ProductId;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_profile_is_deployable")]
    public static int ContextProfileIsDeployable(IntPtr context, int index)
    {
        try
        {
            var profile = ProfileAt(context, index);
            return profile == null ? -1 : (profile.IsDeployable ? 1 : 0);
        }
        catch (Exception ex) { return Fail(ex); }
    }

    // ── Controller ──────────────────────────────────────────────────────

    private sealed class ControllerBinding
    {
        public required GKController Controller;
        public Action<GKController, GKOutputPacket>? Handler;
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_context_create_controller")]
    public static IntPtr ContextCreateController(IntPtr context, IntPtr profileId)
    {
        try
        {
            var ctx = FromHandle<GKContext>(context);
            if (ctx == null) { SetError("invalid context handle"); return IntPtr.Zero; }
            var id = ReadUtf8(profileId);
            if (string.IsNullOrEmpty(id)) { SetError("profile id is null or empty"); return IntPtr.Zero; }
            var profile = ctx.GetProfile(id);
            if (profile == null) { SetError($"profile '{id}' is not loaded"); return IntPtr.Zero; }
            var controller = ctx.CreateController(profile);
            return ToHandle(new ControllerBinding { Controller = controller });
        }
        catch (Exception ex) { Fail(ex); return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_controller_destroy")]
    public static void ControllerDestroy(IntPtr controller)
    {
        var binding = FromHandle<ControllerBinding>(controller);
        FreeHandle(controller);
        if (binding == null) return;
        try
        {
            if (binding.Handler != null)
                binding.Controller.OutputReceived -= binding.Handler;
            binding.Controller.Dispose();
        }
        catch (Exception ex) { SetError(ex.ToString()); }
    }

    private static GKController? ControllerOf(IntPtr controller)
    {
        var binding = FromHandle<ControllerBinding>(controller);
        if (binding == null) SetError("invalid controller handle");
        return binding?.Controller;
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_controller_submit_gamepad")]
    public static int ControllerSubmitGamepad(IntPtr controller, GkmdGamepadState* state)
    {
        try
        {
            if (state == null) { SetError("state is null"); return -1; }
            var ctl = ControllerOf(controller);
            if (ctl == null) return -1;

            var managed = new GKGamepadState
            {
                Buttons = (GKButton)state->Buttons,
                Hat = (GKHat)state->Hat,
                HatDegrees = state->HasHatDegrees != 0 ? state->HatDegrees : null,
                HatHundredths = state->HasHatHundredths != 0 ? state->HatHundredths : null,
                HatRaw = state->HasHatRaw != 0 ? state->HatRaw : null,
                TouchpadFinger0Active = state->TouchpadFinger0Active != 0,
                TouchpadFinger0X = state->TouchpadFinger0X,
                TouchpadFinger0Y = state->TouchpadFinger0Y,
                TouchpadFinger0Id = state->TouchpadFinger0Id,
                TouchpadFinger1Active = state->TouchpadFinger1Active != 0,
                TouchpadFinger1X = state->TouchpadFinger1X,
                TouchpadFinger1Y = state->TouchpadFinger1Y,
                TouchpadFinger1Id = state->TouchpadFinger1Id,
                TouchpadPacketCounter = state->TouchpadPacketCounter,
                GyroPitch = state->GyroPitch,
                GyroYaw = state->GyroYaw,
                GyroRoll = state->GyroRoll,
                AccelX = state->AccelX,
                AccelY = state->AccelY,
                AccelZ = state->AccelZ,
                SensorTimestamp = state->SensorTimestamp,
                AccelGX = state->AccelGX,
                AccelGY = state->AccelGY,
                AccelGZ = state->AccelGZ,
                GyroDpsX = state->GyroDpsX,
                GyroDpsY = state->GyroDpsY,
                GyroDpsZ = state->GyroDpsZ,
                BatteryLevel = state->BatteryLevel,
                BatteryCharging = state->BatteryCharging != 0,
                BatteryFull = state->BatteryFull != 0,
                MicMuted = state->MicMuted != 0,
                HeadphonesConnected = state->HeadphonesConnected != 0,
            };

            if (state->Axes != null && state->AxisCount > 0)
            {
                var axes = new Dictionary<GKAxis, float>(state->AxisCount);
                for (int i = 0; i < state->AxisCount; i++)
                    axes[(GKAxis)state->Axes[i].Axis] = state->Axes[i].Value;
                managed.Axes = axes;
            }

            ctl.SubmitState(managed);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_controller_submit_raw")]
    public static int ControllerSubmitRaw(IntPtr controller, byte* data, int length)
    {
        try
        {
            if (data == null || length <= 0) { SetError("data is null or empty"); return -1; }
            var ctl = ControllerOf(controller);
            if (ctl == null) return -1;
            ctl.SubmitRawReport(new ReadOnlySpan<byte>(data, length));
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_controller_set_output_callback")]
    public static int ControllerSetOutputCallback(
        IntPtr controller, IntPtr callback, IntPtr userData)
        => SetOutputCallbackCore(controller, callback, userData);

    private static int SetOutputCallbackCore(
        IntPtr controller, IntPtr callback, IntPtr userData)
    {
        try
        {
            var binding = FromHandle<ControllerBinding>(controller);
            if (binding == null) { SetError("invalid controller handle"); return -1; }

            if (binding.Handler != null)
            {
                binding.Controller.OutputReceived -= binding.Handler;
                binding.Handler = null;
            }

            if (callback == IntPtr.Zero) return 0;

            var fn = (delegate* unmanaged[Cdecl]<
                uint, byte, byte*, int, uint, IntPtr, void>)callback;

            binding.Handler = (_, packet) =>
            {
                var span = packet.Data.Span;
                fixed (byte* p = span)
                {
                    fn((uint)packet.Source, packet.ReportId, p,
                        span.Length, packet.SeqNo, userData);
                }
            };
            binding.Controller.OutputReceived += binding.Handler;
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "gkmd_controller_clear_output_callback")]
    public static int ControllerClearOutputCallback(IntPtr controller)
        => SetOutputCallbackCore(controller, IntPtr.Zero, IntPtr.Zero);
}
