using System;
using System.Collections.Generic;
using System.Threading;
using GKMD.Internal;

namespace GKMD;

/// <summary>
/// A live virtual controller. Created by <see cref="GKContext.CreateController"/>;
/// dispose to remove the device. The controller exposes two channels:
///
/// <para><b>Input</b> (host → game): the consumer pushes <see cref="GKGamepadState"/>
/// frames via <see cref="SubmitState"/> at whatever rate they want — typically the
/// rate of their real input source. The SDK translates the abstract state into the
/// profile's native HID descriptor format and writes it to a shared section that
/// the kernel-side driver reads at ~250 Hz. There is no internal pumping thread;
/// the consumer drives the cadence.</para>
///
/// <para><b>Output</b> (game → host): the SDK runs a background reader thread that
/// captures rumble / haptics / FFB / LED commands from any host application and
/// raises <see cref="OutputReceived"/>. Since issue #34 the thread blocks on a
/// driver-signaled event (falling back to an 8 ms poll against older drivers).
/// Handlers run on the reader thread, not the consumer's UI thread. Implement
/// your handler accordingly.</para>
/// </summary>
public sealed class GKController : IDisposable
{
    private readonly GKContext _context;
    internal int Index { get; }
    internal string? InstanceId { get; }
    public GKProfile Profile { get; }

    // Issue #39: non-null when this controller runs on the USB/IP backend.
    // The backend's device emulator speaks the SAME shared-memory contract
    // (it reads the input section this class writes and publishes to the
    // output ring this class's poll loop reads), which is why nothing else
    // in this class changes per backend. Dispose routing in GKContext keys on it.
    internal Internal.Usbip.UsbipBackendHandle? UsbipHandle { get; }

    /// <summary>Issue #39: the audio surfaces of a composite USB persona
    /// (speaker/haptic PCM out, microphone in, volume/mute control
    /// events). Null for personas with no audio streaming interface.</summary>
    public GKUsbAudio? UsbAudio { get; }

    // Encoder built once from the profile descriptor at construction time;
    // SubmitState reuses it for every frame.
    private readonly HidReportBuilder? _reportBuilder;
    private readonly IntPtr _inputView;
    // Named auto-reset event signaled by WriteInputFrame so the USB/IP
    // device emulator's input pump can wake immediately instead of
    // busy-polling. Cached at construction time alongside the view pointer.
    private readonly IntPtr _inputEvent;
    private uint _inputSeqNo;

    // Output passthrough reader (rumble/haptics/FFB) — background thread
    // poll-reads the per-controller output section and raises OutputReceived.
    private readonly IntPtr _outputView;
    private readonly Thread? _outputThread;
    private readonly CancellationTokenSource _outputCts = new();

    // 14-byte GIP-format buffer reused per frame to avoid per-call alloc.
    // Legacy artifact: this slice existed for the removed XUSB companion
    // that serviced IOCTL_XUSB_GET_STATE. GKMD has no such companion, and
    // under the USB/IP backend the buffer is never packed (see
    // _packsGipBuffer), so it always stays zeroed. It is retained only to
    // keep the write path shape unchanged.
    //
    // Layout (matches the proven pre-SDK test app):
    //   [0..1]  LX  16-bit unsigned (0..65535)
    //   [2..3]  LY  16-bit unsigned
    //   [4..5]  RX  16-bit unsigned
    //   [6..7]  RY  16-bit unsigned
    //   [8..9]  LT  10-bit unsigned in the low bits
    //   [10..11] RT 10-bit unsigned in the low bits
    //   [12]    btnLow  (A=0x01 B=0x02 X=0x04 Y=0x08 LB=0x10 RB=0x20 LS=0x40 RS=0x80)
    //   [13]    btnHigh (Back=0x01 Start=0x02 …)
    private readonly byte[] _gipBuf = new byte[14];

    // v1.3.0 — per-controller reusable HID input report buffer. SubmitState
    // calls BuildReportInto(_reportBuffer, ...) instead of BuildReport which
    // allocates a fresh byte[] each frame. At 250 Hz × N controllers the
    // alloc churn was real GC pressure; reusing avoids it entirely.
    // Sized at HidReportBuilder.InputReportByteSize, computed in the ctor.
    private readonly byte[] _reportBuffer;

    // v1.3.0 — per-controller reusable raw report buffer. SubmitRawReport
    // (DualSense / vendor-protocol path) used to do report.ToArray() per
    // call; this 64-byte buffer absorbs the copy without the alloc churn.
    private readonly byte[] _rawReportBuffer = new byte[64];

    /// <summary>Raised on the SDK's output-polling thread whenever a host
    /// application sends a rumble, haptic, FFB, feature, or LED command to
    /// this virtual controller. Subscribers must be thread-safe.
    ///
    /// <para><b>Cadence and ordering:</b> the reader wakes on the
    /// per-packet event signal (issue #34; 8 ms poll fallback against
    /// older backends) and drains every slot written since the last wake,
    /// in monotonic SeqNo order. Multiple <c>OutputReceived</c> invocations
    /// per wake are normal. DirectInput PID FFB writes 3 packets in
    /// 1-3 ms (Set Effect → Set Constant Force → Effect Operation Start)
    /// and all three surface here.</para>
    ///
    /// <para><b>Ring depth:</b> 64 slots × 256-byte payload. If the
    /// consumer's handler stalls for &gt; 512 ms while the backend is
    /// writing at burst rate, the oldest packets get overwritten —
    /// keep the handler cheap (no synchronous I/O, no long locks).
    /// Pre-1.1.40 was a single-slot channel that silently coalesced
    /// back-to-back writes; that drop pattern is fixed.</para></summary>
    public event Action<GKController, GKOutputPacket>? OutputReceived;

    /// <summary>v1.3.5 — raised when an inbound output report matches the
    /// profile's <see cref="GKProfile.HasExtendedOutput"/> spec. The SDK
    /// decodes the bytes per the profile's <c>extendedOutputReport</c> field
    /// list and surfaces parsed values (rumble amplitudes, lightbar RGB,
    /// adaptive-trigger blocks, etc.) keyed by semantic name.
    ///
    /// <para>Consumers that want raw bytes still get them via
    /// <see cref="OutputReceived"/> — both events fire for matching reports.
    /// Subscribers must be thread-safe (raised on the polling thread).</para></summary>
    public event EventHandler<GKOutputDecodedEventArgs>? OutputDecoded;

    // v1.3.5 — vendor-blob input encoder state. Built lazily when the profile
    // declares extendedReport. Holds rolling counters (Sony's framingTag /
    // reportCounter increment monotonically across SubmitState calls).
    private VendorBlobCodec.EncoderState? _extEncoderState;

    // v1.3.5 — vendor-blob output encoder state. Allocated lazily on the
    // first EncodeOutput call so consumers that never call it (input-only
    // virtuals, output-via-OnOutputReceived consumers) skip the dictionary
    // alloc. Holds rolling counters for output direction — Sony BT effect
    // output's btTag increments stride-16 per write or real firmware drops
    // the packet.
    private VendorBlobCodec.EncoderState? _outputEncoderState;
    private readonly object _outputEncoderStateLock = new();

    // v1.3.5 — buffer sized to ExtendedReport.Size, allocated once. NULL
    // when the profile has no extendedReport.
    private byte[]? _extendedReportBuffer;

    // v1.3.5 — host-side arm flag. False until a host write matches one of
    // ExtendedReport.armOn triggers; true thereafter for the lifetime of
    // this controller. Until armed, SubmitState falls through to the
    // descriptor-driven BuildReportInto path so consumers that never issue
    // the handshake still see legacy Report 1 emission.
    private volatile bool _extendedModeArmed;

    // Cached layout projections (audit 1n): Profile.Sticks / Profile.Triggers
    // allocate on every access, so snapshot them once in the ctor and read
    // the cached lists on the SubmitState hot path. The profile's layout is
    // immutable for the controller's lifetime.
    private readonly System.Collections.Generic.IReadOnlyList<GKSimpleStick> _cachedSticks;
    private readonly System.Collections.Generic.IReadOnlyList<GKSimpleTrigger> _cachedTriggers;

    // Legacy companion input doorbell (perf audit 2026-07-21), retained from
    // the removed XUSB companion path: signaled per GIP-carrying frame to
    // complete a parked WAIT_FOR_INPUT at frame arrival. Always IntPtr.Zero
    // under the USB/IP backend.
    private readonly IntPtr _companionInputEvent;

    // Canonical trigger positions, resolved once (issue #34). The axisMap
    // walk (case-insensitive role compare + hex key parse) ran inside
    // every SubmitState, twice, for values that are constant per profile.
    private readonly GKAxis _canonicalLt;
    private readonly GKAxis _canonicalRt;

    /// <summary>Optional diagnostic: invoked at the end of every successful
    /// <see cref="SubmitState"/> with the elapsed microseconds. Wire this
    /// when investigating per-frame submit latency (e.g. issue #21 USB
    /// stalls). Called inline on the caller's thread; keep the handler
/// short — log to a ring buffer or counter, don't block.</summary>
    public Action<long>? OnSubmitLatencyMicros { get; set; }


    // T26-2 — set once at ctor, read every frame in SubmitState. The
    // GIP-format byte slice was only meaningful on the legacy (non-USB/IP)
    // path; GKMD runs purely on USB/IP, so _packsGipBuffer is always false
    // and the per-frame packing AND the 14-byte Marshal.Copy are skipped
    // entirely (~60-80 instructions saved per frame).
    private readonly bool _packsGipBuffer;

    internal GKController(GKContext context, int index, GKProfile profile, string? instanceId,
                          Internal.Usbip.UsbipBackendHandle? usbipHandle = null)
    {
        _context = context;
        Index = index;
        InstanceId = instanceId;
        Profile = profile;
        UsbipHandle = usbipHandle;
        if (usbipHandle != null)
        {
            var cfg = profile.Inner.UsbConfiguration;
            bool hasAudioStream = false;
            if (cfg != null)
            {
                foreach (var iface in cfg.Interfaces)
                {
                    if (iface.Function == "audioStreamingOut" || iface.Function == "audioStreamingIn")
                    {
                        hasAudioStream = true;
                        break;
                    }
                }
            }
            if (hasAudioStream)
                UsbAudio = new GKUsbAudio(profile.Inner, usbipHandle.Device);
        }

        // v1.3.0 T10 — cached per-profile builder; same descriptor + same
        // maps produce identical output, so each CreateController for a
        // given profile reuses the same configured builder instead of
        // re-parsing the descriptor on every ctor.
        // Vendor profiles (e.g. Xbox 360) have no HID descriptor, so
        // GetOrBuildReportBuilder returns null — we handle them by using
        // InputReportSize for buffer sizing and skipping HidReportBuilder logic.
        _reportBuilder = profile.Inner.GetOrBuildReportBuilder();
        int reportSize = _reportBuilder != null ? _reportBuilder.InputReportByteSize : (profile.Inner.InputReportSize ?? 64);
        _reportBuffer = new byte[reportSize];
        _rawReportBuffer = new byte[reportSize];

        // Snapshot the layout projections once (audit 1n): see the field
        // declarations. Profile.Sticks/Triggers allocate per access.
        _cachedSticks = profile.Sticks;
        _cachedTriggers = profile.Triggers;

        // Resolve the canonical trigger axes once (issue #34): axisMap-
        // declared override wins, else the Z/Rz defaults PadForge writes.
        _canonicalLt = ResolveCanonicalAxis(profile.Inner.AxisMap, "lefttrigger", GKAxis.Z);
        _canonicalRt = ResolveCanonicalAxis(profile.Inner.AxisMap, "righttrigger", GKAxis.Rz);

        // The GIP-format buffer slice was only read by the removed XUSB
        // companion. GKMD has no companion and always attaches through the
        // USB/IP backend (UsbipHandle != null), so the slice is unused and
        // the 14-byte packing is skipped. RequiresXusbCompanion is kept as
        // the legacy predicate for the now-dead non-USB/IP path.
        _packsGipBuffer = UsbipHandle == null && profile.Inner.RequiresXusbCompanion;
        _inputView = SharedMemoryIO.EnsureInputMapping(index);
        _inputEvent = SharedMemoryIO.GetInputEvent(index);
        _companionInputEvent = _packsGipBuffer
            ? SharedMemoryIO.GetCompanionInputEvent(index) : IntPtr.Zero;

        // v1.3.5 — pre-allocate vendor-blob buffer + encoder state ONLY when
        // the profile actually arms (Sony BT post-handshake). Profiles with
        // extendedReport metadata but no armOn list (every USB Sony profile,
        // every generic profile) never run the codec, so the buffer alloc
        // would be dead memory and the SubmitState hot path would carry an
        // unused extended-write branch. Issue #21 USB jerkiness: keeping
        // this allocation off entirely on USB profiles is the difference
        // between v1.3.4-equivalent hot-path codegen and the regressed path.
        // alwaysArmed profiles (Switch 2 Pro) skip the handshake entirely:
        // their real firmware streams the vendor report from power-on, and
        // their descriptor's FIRST declared input report is an opaque
        // vendor blob, so the legacy fallback path has no buttons or axes
        // to fill and every consumer would read a live device that never
        // moves.
        bool alwaysArmed = profile.ExtendedReport?.AlwaysArmed == true;
        bool armOnDeclared = profile.ExtendedReport?.ArmOn != null
                          && profile.ExtendedReport.ArmOn.Count > 0;
        if (profile.ExtendedReport != null && (armOnDeclared || alwaysArmed))
        {
            _extendedReportBuffer = new byte[profile.ExtendedReport.Size];
            _extEncoderState = new VendorBlobCodec.EncoderState();
            _extendedModeArmed = alwaysArmed;
        }

        // Output passthrough is best-effort. If the section can't be created
        // (rare — only LocalService permission issues) we just don't raise
        // OutputReceived events.
        try
        {
            _outputView = SharedMemoryIO.EnsureOutputMapping(index);
            _outputThread = new Thread(OutputPollLoop)
            {
                IsBackground = true,
                Name = $"GKOutputReader_{index}",
            };
            _outputThread.Start();
            // Publish the thread so any unmap stops it first. Without this a
            // sweep that runs while this controller is still alive frees the
            // view underneath the loop below (issue #45).
            SharedMemoryIO.RegisterOutputPump(index, _outputCts, _outputThread);
        }
        catch
        {
            _outputView = IntPtr.Zero;
        }
    }

    // Resolve a canonical trigger axis from a profile's axisMap: the
    // axisMap-declared role position wins, else the given default. Runs
    // ONCE per controller at construction (issue #34). The walk's
    // case-insensitive compares and hex parse used to run inside every
    // SubmitState, twice, for a per-profile constant.
    internal static GKAxis ResolveCanonicalAxis(
        Dictionary<string, string>? axisMap,
        string roleName,
        GKAxis canonicalDefault)
    {
        if (axisMap == null) return canonicalDefault;
        // LAST match wins on duplicate roles (audit of #34): ApplyAxisMap
        // assigns semantic slots last-wins, so canonical resolution must
        // agree or a malformed duplicate-role map would read one axis and
        // write another. Shipped maps declare each role once.
        GKAxis resolved = canonicalDefault;
        foreach (var kvp in axisMap)
        {
            if (kvp.Value == null) continue;
            if (!string.Equals(kvp.Value, roleName, StringComparison.OrdinalIgnoreCase))
                continue;
            ushort usage;
            try { usage = Convert.ToUInt16(kvp.Key, 16); }
            catch { continue; }
            if (usage <= 0xFF) usage |= 0x0100;
            resolved = (GKAxis)usage;
        }
        return resolved;
    }

    // Canonical-first, field-key-fallback trigger resolution. PadForge writes
    // axes[Z]/axes[Rz] via ResolveAxisByRole canonical defaults; the
    // GKMDTest / StandardAxes path writes axes[layout.triggers[N].Axis]
    // (Vx/Vy for the unified xbox-360-* profiles). Both feed the GIP buffer
    // used by the Xbox-VID path. Same resolution rule that HidReportBuilder's
    // combined-Z synthesis uses.
    // The canonical axis arrives pre-resolved (ctor-cached, issue #34).
    internal static double ResolveTrigger(
        Dictionary<GKAxis, float>? axes,
        IReadOnlyList<GKSimpleTrigger> triggers,
        int slot,
        GKAxis canonical)
    {
        if (axes != null)
        {
            if (axes.TryGetValue(canonical, out var vCanon))
                return Math.Clamp(vCanon, 0f, 1f);
            if (slot < triggers.Count && axes.TryGetValue(triggers[slot].Axis, out var vField))
                return Math.Clamp(vField, 0f, 1f);
        }
        return 0.0;
    }

    /// <summary>Push the next input frame to the virtual controller.
    /// The SDK encodes <paramref name="state"/> into the active profile's
    /// HID report layout and publishes it via shared memory.</summary>
    public void SubmitState(in GKGamepadState state)
    {
        ThrowIfDisposed();

        long startTicks = OnSubmitLatencyMicros != null
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // v1.3.9 — single unified state.Axes dict drives every analog input.
        // Resolve the 6 "simple-slot" values (left stick X/Y, right stick
        // X/Y, LT, RT) by looking up each profile's declared sticks/triggers
        // in the axes dict. Auto-default: 0.5 (centered) for sticks, 0.0
        // (released) for triggers.
        var axes = state.Axes;
        double GetAxis(GKAxis ax, double def) =>
            axes != null && axes.TryGetValue(ax, out var v) ? Math.Clamp(v, 0f, 1f) : def;

        // Cached at construction: Profile.Sticks / Profile.Triggers are
        // uncached computed properties that each allocate a List plus
        // record elements on every access. Reading them per SubmitState
        // frame (~250 Hz × N controllers) is ~6 heap objects/frame of
        // avoidable GC pressure on the hot path (audit 1n). The layout is
        // immutable for the controller's lifetime, so resolve once.
        var sticks = _cachedSticks;
        var triggers = _cachedTriggers;
        double mlx = sticks.Count > 0 ? GetAxis(sticks[0].XAxis, 0.5) : 0.5;
        double mly = sticks.Count > 0 ? GetAxis(sticks[0].YAxis, 0.5) : 0.5;
        double mrx = sticks.Count > 1 ? GetAxis(sticks[1].XAxis, 0.5) : 0.5;
        double mry = sticks.Count > 1 ? GetAxis(sticks[1].YAxis, 0.5) : 0.5;
        // Triggers resolve canonical-first, then fall back to the descriptor's
        // declared trigger field. PadForge writes axes[Z]/axes[Rz] via
        // ResolveAxisByRole (canonical defaults when no axisMap); GKMDTest
        // / StandardAxes writes axes[triggers[N].Axis] directly. Both must feed
        // the GIP buffer that drives XInput / WGI / RawInput; otherwise the
        // unified xbox-360-* descriptors (layout.triggers = Vx/Vy) silently
        // drop PadForge's writes and triggers freeze in non-DirectInput APIs.
        double mlt = ResolveTrigger(axes, triggers, 0, _canonicalLt);
        double mrt = ResolveTrigger(axes, triggers, 1, _canonicalRt);

        byte[] report;
        // v1.3.5 — vendor-blob path is gated on the host-side arm flag.
        // Sony BT controllers default to legacy short Report 0x01; the host
        // (Steam Input, Chrome's Gamepad API, dualsense-tester, ds.daidr.me)
        // issues a Get_Feature on 0x05 / 0x09 / 0x20 to switch real firmware
        // into vendor-blob mode (Report 0x31 / 0x11). The arm-watcher in
        // OutputPollLoop flips _extendedModeArmed when any of those reads
        // arrives via HidFeatureRead. Until then, fall through to the
        // descriptor-driven BuildReportInto path so joy.cpl, RawInput, and
        // generic HID consumers see structured X/Y/Rx/Ry through Report 0x01.
        // A profile whose extendedReport sets alwaysArmed starts armed and
        // never takes the legacy path, for controllers that stream the
        // vendor report from power-on rather than switching into it.
        // The codec runs only after the host-side arm-handshake has fired.
        // Profiles without an armOn list (every USB Sony profile, every
        // generic profile) never arm, so they always take the legacy
        // BuildReportInto path — same code path v1.3.4 used. This avoids
        // the per-frame codec cost (field-list walk, CRC compute, byte
        // re-encode) on the 250 Hz SubmitState hot path for profiles that
        // don't need vendor-blob input emission. Bug #21: pre-v1.3.5 USB
        // profiles had no extendedReport at all and this gate didn't apply;
        // 0cec81d added extendedReport metadata to USB Sony profiles for
        // PadForge's bidirectional decode, which silently flipped USB onto
        // the codec path even though USB doesn't need vendor-blob input
        // (its descriptor already declares structured X/Y/Rx/Ry usages
        // that joy.cpl, dinput, and the test app's parsers all decode
        // correctly). Restoring the v1.3.4 path for USB removes the
        // regression.
        bool useExtended = Profile.ExtendedReport != null
                        && _extendedReportBuffer != null
                        && _extEncoderState != null
                        && _extendedModeArmed;

        if (useExtended)
        {
            // Profile.ExtendedReport's field list drives byte placement.
            // Sticks / triggers / buttons / hat encode through
            // VendorBlobCodec; CRC32 (if declared) is computed last. For
            // Sony BT, the buffer is full 78 bytes including byte[0] = RID
            // (0x31 / 0x11), so the driver must NOT prepend its own RID.
            // The driver-side WriteToInputReport recognizes the extended
            // path via the SHARED_INPUT.ExtendedReportSize > 0 hint set
            // alongside the legacy bytes below.
            VendorBlobCodec.EncodeInput(Profile.ExtendedReport!, in state,
                (float)mlx, (float)mly, (float)mrx, (float)mry, (float)mlt, (float)mrt,
                _extendedReportBuffer!, _extEncoderState!);
            report = _extendedReportBuffer!;
        }
        else
        {
            // Vendor profiles (e.g. Xbox 360) have no HidReportBuilder —
            // they use SubmitRawReport exclusively. SubmitState is a no-op.
            if (_reportBuilder == null) return;
            
            // v1.3.9 — unified axes dict drives every declared analog input.
            // Hat priority chain (HatDegrees > HatHundredths > HatRaw > Hat)
            // picks the first non-null and ignores the rest.
            _reportBuilder.BuildReportInto(_reportBuffer,
                axes: state.Axes,
                hatValue: (int)state.Hat,
                buttonMask: (uint)state.Buttons,
                hatDegrees: state.HatDegrees,
                hatHundredths: state.HatHundredths,
                hatRaw: state.HatRaw);

            // v1.3.5 — overlay profile-declared fixed bytes (e.g. DS5 Edge
            // USB activeProfile = 0x80 at byte 49 so dualsense-tester's
            // useInNormalMode check `byte && (byte & 3) === 0` succeeds —
            // see the DS5 Edge profile's inputDefaults). Codec
            // path doesn't need this: it walks ExtendedReport.fields which
            // already lists these as uint8 entries with `initial` values,
            // so the constants participate in CRC32 computation. Legacy
            // path has no CRC, so a post-encode overlay is fine.
            var inputDefaults = Profile.Inner.InputDefaults;
            if (inputDefaults != null)
            {
                int len = _reportBuffer.Length;
                foreach (var p in inputDefaults)
                {
                    if ((uint)p.Byte < (uint)len)
                        _reportBuffer[p.Byte] = (byte)p.Value;
                }
            }

            report = _reportBuffer;
        }

        // T26-2 — pack the GIP-format buffer ONLY for Xbox-VID profiles on
        // the legacy path. Under the USB/IP backend _packsGipBuffer is
        // always false, so the bytes are unused and the per-frame packing is
        // skipped entirely (~60-80 instructions saved). The downstream
        // Marshal.Copy is also skipped via the gipData=null path in
        // WriteInputFrame.
        if (_packsGipBuffer)
        {
            ushort gipLx = (ushort)(mlx * 65535);
            ushort gipLy = (ushort)(mly * 65535);
            ushort gipRx = (ushort)(mrx * 65535);
            ushort gipRy = (ushort)(mry * 65535);
            ushort gipLt = (ushort)(mlt * 1023);
            ushort gipRt = (ushort)(mrt * 1023);
            _gipBuf[0]  = (byte)(gipLx & 0xFF); _gipBuf[1]  = (byte)(gipLx >> 8);
            _gipBuf[2]  = (byte)(gipLy & 0xFF); _gipBuf[3]  = (byte)(gipLy >> 8);
            _gipBuf[4]  = (byte)(gipRx & 0xFF); _gipBuf[5]  = (byte)(gipRx >> 8);
            _gipBuf[6]  = (byte)(gipRy & 0xFF); _gipBuf[7]  = (byte)(gipRy >> 8);
            _gipBuf[8]  = (byte)(gipLt & 0xFF); _gipBuf[9]  = (byte)(gipLt >> 8);
            _gipBuf[10] = (byte)(gipRt & 0xFF); _gipBuf[11] = (byte)(gipRt >> 8);
            // Button low byte: A,B,X,Y,LB,RB,LS,RS (XInput XUSB convention)
            uint b = (uint)state.Buttons;
            byte btnLow = 0;
            if ((b & (uint)GKButton.A)           != 0) btnLow |= 0x01;
            if ((b & (uint)GKButton.B)           != 0) btnLow |= 0x02;
            if ((b & (uint)GKButton.X)           != 0) btnLow |= 0x04;
            if ((b & (uint)GKButton.Y)           != 0) btnLow |= 0x08;
            if ((b & (uint)GKButton.LeftBumper)  != 0) btnLow |= 0x10;
            if ((b & (uint)GKButton.RightBumper) != 0) btnLow |= 0x20;
            if ((b & (uint)GKButton.LeftStick)   != 0) btnLow |= 0x40;
            if ((b & (uint)GKButton.RightStick)  != 0) btnLow |= 0x80;
            _gipBuf[12] = btnLow;
            // Button high byte. Bits 0..1 are Back/Start, bits 2..5 carry the
            // 4-bit hat, and Guide sits above the hat at bit 6 (0x40). The
            // removed XUSB companion used to translate 0x40 to the
            // undocumented XINPUT_GAMEPAD_GUIDE bit (0x0400) returned by
            // XInputGetStateEx.
            // Pre-v1.3.3 the hat bits were never written, so XInput consumers
            // hitting xusb22 directly (SDL3 XInput backend, sample-quality
            // XInput apps) saw no d-pad on Xbox 360 wired (#19). HID-derived
            // consumers (joy.cpl/DI, SDL3-HID, browsers via WGI) were
            // unaffected because BuildReportInto correctly populates the
            // descriptor's Hat Switch usage. Mask against 0x0F so a future
            // GKHat extension can't smear into Back/Start bits below.
            byte btnHigh = 0;
            if ((b & (uint)GKButton.Back)  != 0) btnHigh |= 0x01;
            if ((b & (uint)GKButton.Start) != 0) btnHigh |= 0x02;
            btnHigh |= (byte)(((byte)state.Hat & 0x0F) << 2);
            if ((b & (uint)GKButton.Guide) != 0) btnHigh |= 0x40;
            _gipBuf[13] = btnHigh;
        }

        // v1.3.5 — two write paths, mutually exclusive per frame:
        //
        //  • Legacy (default, _extendedModeArmed=false or no ExtendedReport):
        //    SDK strips the Report ID byte at position 0 and the driver
        //    re-prepends ctx->FirstInputReportId. Result: the descriptor's
        //    first declared input report ID arrives at the kernel HID stack
        //    (e.g. Report 0x01 for Sony BT). joy.cpl, RawInput, and generic
        //    HID consumers see structured X/Y/Rx/Ry per the legacy descriptor.
        //
        //  • Extended (post-arm, useExtended=true): SDK passes the full
        //    RID-included buffer (e.g. 78-byte Sony BT Report 0x31 with
        //    CRC32 trailer) via WriteInputFrame's extendedData parameter.
        //    Driver emits ExtendedReportData verbatim (no RID prepend).
        //    Steam Input, dualsense-tester, ds.daidr.me, and Chrome's
        //    Gamepad API decode the vendor-blob format. joy.cpl loses
        //    sticks in this state — same as real Sony hardware behavior
        //    once Steam runs and switches the controller to extended mode.
        //
        // dataLen capped at SharedMemoryIO.DATA_CAPACITY (256 bytes; widened
        // from 64 in 2026-04-23). T26-2 — pass null for gipData on non-Xbox
        // profiles so WriteInputFrame skips the 14-byte Marshal.Copy.
        if (useExtended)
        {
            int extLen = Profile.ExtendedReport!.Size;
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo,
                Array.Empty<byte>(), 0,
                _packsGipBuffer ? _gipBuf : null,
                companionEvent: _companionInputEvent,
                dataOffset: 0,
                extendedData: report, extendedLen: extLen);
        }
        else
        {
            int dataStart = _reportBuilder!.InputReportId != 0 ? 1 : 0;
            int dataLen = Math.Min(report.Length - dataStart, SharedMemoryIO.DATA_CAPACITY);
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo, report, dataLen,
                _packsGipBuffer ? _gipBuf : null, dataStart,
                companionEvent: _companionInputEvent);
        }

        if (OnSubmitLatencyMicros != null)
        {
            long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
            long micros = elapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            OnSubmitLatencyMicros(micros);
        }
    }

    /// <summary>Push a raw HID input report for features that
    /// <see cref="GKGamepadState"/> doesn't model — touchpad coordinates,
    /// gyroscope, sensor packets, vendor extensions.
    ///
    /// <para>Pass <b>data bytes only</b> — do NOT include a Report ID prefix.
    /// The driver prepends the Report ID automatically (same as
    /// <see cref="SubmitState"/>). For a DualSense with Report ID 0x01 and
    /// 64-byte InputReportByteLength, pass 63 bytes of data.</para>
    ///
    /// <para>For profiles with no Report ID (e.g. Xbox Series BT), pass the
    /// full report as-is.</para>
    ///
    /// <para>Tip: use <see cref="GKProfile.InputReportSize"/> and
    /// <see cref="GKProfile.GetDescriptorBytes"/> to determine the expected
    /// data layout. The test app's <c>info</c> command shows every field's
    /// bit offset.</para>
    /// </summary>
    public void SubmitRawReport(ReadOnlySpan<byte> report)
    {
        ThrowIfDisposed();
        if (report.Length == 0) throw new ArgumentException("Report cannot be empty.", nameof(report));
        if (report.Length > SharedMemoryIO.DATA_CAPACITY)
            throw new ArgumentException(
                $"Report length {report.Length} exceeds the {SharedMemoryIO.DATA_CAPACITY}-byte shared section payload.",
                nameof(report));

        // v1.3.0 — copy into the per-controller reusable buffer instead of
        // report.ToArray()'ing per call. Vendor-protocol consumers (PadForge
        // DualSense path, etc.) hit this path at the same rate as
        // SubmitState; the alloc-per-call cost was visible.
        report.CopyTo(_rawReportBuffer.AsSpan());

        // v1.3.5 — overlay profile-declared fixed bytes. Note that
        // SubmitRawReport's `report` arg is DATA-ONLY (no report ID byte
        // prepended); inputDefaults entries are JSON-keyed by ON-WIRE byte
        // (where byte 0 is the report ID), so we subtract 1 to land in
        // the data-buffer coordinate system. PadForge's USB DS5 raw packers
        // build the standard Sony layout but don't know about Edge-specific
        // status bytes (activeProfile at struct[48]); without overlaying
        // here, SubmitRawReport clobbers whatever SubmitState wrote a few
        // microseconds earlier.
        var rawDefaults = Profile.Inner.InputDefaults;
        if (rawDefaults != null)
        {
            int len = Math.Min(report.Length, _rawReportBuffer.Length);
            int dataShift = 0;
            if (_reportBuilder != null)
            {
                byte rid = (byte)(_reportBuilder.InputReportId);
                dataShift = rid != 0 ? 1 : 0;
            }
            foreach (var p in rawDefaults)
            {
                int idx = p.Byte - dataShift;
                if ((uint)idx < (uint)len)
                    _rawReportBuffer[idx] = (byte)p.Value;
            }
        }
        // Raw mode reuses the GIP buffer at whatever state SubmitState last
        // left it in (or zero if SubmitState was never called) — raw consumers
        // are expected to also call SubmitState if they need GIP/XInput.
        // T30-2 — pass null for gipData on non-Xbox profiles, same logic as
        // SubmitState's Xbox-only GIP packing. Saves the 14-byte Marshal.Copy
        // per raw frame on DualSense / Switch Pro / generic gamepad paths.
        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo, _rawReportBuffer, report.Length,
            _packsGipBuffer ? _gipBuf : null,
            companionEvent: _companionInputEvent);
    }

    /// <summary>v1.3.5 — instance-level <see cref="GKOutputEncoder.Encode"/>
    /// that threads per-controller rolling-counter state through the codec.
    ///
    /// <para>Required for DS5 BT effect output: the spec's <c>btTag</c> field
    /// is a stride-16 rolling counter, and real Sony firmware drops the
    /// effect packet if consecutive writes don't carry the next tag value.
    /// The static <see cref="GKOutputEncoder.Encode"/> overload is stateless
    /// and falls back to <c>initial</c>; use this method instead so the
    /// SDK owns the increment.</para>
    ///
    /// <para>Per-controller — multiple virtuals never share counter state.
    /// The internal lock makes this safe to call from any thread.</para>
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> if the profile
    /// has no <c>extendedOutputReport</c> spec.</para></summary>
    public byte[] EncodeOutput(IReadOnlyDictionary<string, object> fields)
    {
        ThrowIfDisposed();
        if (fields == null) throw new ArgumentNullException(nameof(fields));

        var spec = Profile.ExtendedOutputReport;
        if (spec == null)
            throw new InvalidOperationException(
                $"Profile '{Profile.Id}' has no extendedOutputReport spec — nothing to encode against.");

        lock (_outputEncoderStateLock)
        {
            _outputEncoderState ??= new VendorBlobCodec.EncoderState();
            return VendorBlobCodec.EncodeOutput(spec, fields, _outputEncoderState);
        }
    }

    /// <summary>Background reader for the per-controller output shared
    /// section, raising <see cref="OutputReceived"/> for each new packet.
    ///
    /// Event-driven since issue #34: the backend creates
    /// <c>Global\GKMDOutputEvent&lt;N&gt;</c> and signals it after every
    /// published packet, so this thread blocks on the event (500 ms safety
    /// timeout, ~2 wakes/s idle) instead of polling every 8 ms
    /// (125 wakes/s idle). Open success doubles as capability detection:
    /// against an older backend that never created the event, the loop
    /// keeps the historical 8 ms poll cadence and retries the open every
    /// 64 cycles (~0.5 s) in case the backend device finishes starting
    /// after this thread does. Output latency with the event is dispatch
    /// cost instead of up-to-8-ms poll quantization; the drain-to-Head
    /// loop below is unchanged, so burst coalescing behaves identically
    /// in both modes.</summary>
    private void OutputPollLoop()
    {
        if (_outputView == IntPtr.Zero) return;
        // Initialize lastSeq to the current Head so any pre-existing ring
        // contents (stale or legitimate) never fire a spurious
        // OutputReceived for the prior session's data.
        uint lastSeq = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(_outputView, 0);
        byte[] buf = new byte[256];
        var ct = _outputCts.Token;

        IntPtr rawEvt = SharedMemoryIO.TryOpenOutputEvent(Index);
        AutoResetEvent? doorbell = null;
        WaitHandle[]? waitPair = null;
        int openRetryCountdown = 64;
        // Adaptive missed-signal healing (#34 audit): in event mode the
        // 500 ms timeout is supposed to be pure paranoia. If a TIMEOUT
        // wake (not an event wake) finds ring data, some producer
        // advanced Head without signaling (version-skewed companion,
        // event-create failure, a foreign waiter stealing wakes). Drop
        // to the historical 8 ms cadence so delivery and ring headroom
        // return to pre-#34 behavior. A drain aborted by a throwing
        // subscriber also shortens the next wait: the consumed signal
        // can cover a packet the aborted drain never reached.
        int eventTimeoutMs = 500;
        int missedSignalStrikes = 0;
        bool drainFaulted = false;
        bool lastWakeWasTimeout = false;
        if (rawEvt != IntPtr.Zero)
        {
            doorbell = new AutoResetEvent(false);
            doorbell.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(rawEvt, ownsHandle: true);
            waitPair = new WaitHandle[] { ct.WaitHandle, doorbell };
        }
        try
        {
        while (!ct.IsCancellationRequested)
        {
            bool drainedAny = false;
            try
            {
                // v1.1.40: drain the ring on every poll. FFB writes
                // Set Effect → Set Constant Force → Effect Operation Start
                // within 1-3 ms; the pre-1.1.40 single-slot channel was
                // coalescing those bursts vs the 8 ms poll cadence and
                // losing the middle (magnitude) packet.
                while (SharedMemoryIO.TryReadOutputFrame(_outputView, ref lastSeq,
                        out byte source, out byte reportId, out int dataSize, buf))
                {
                    drainedAny = true;
                    var data = new ReadOnlyMemory<byte>(buf, 0, dataSize);
                    var pkt = new GKOutputPacket((GKOutputSource)source, reportId, data, lastSeq);
                    OutputReceived?.Invoke(this, pkt);

                    // v1.3.5 — vendor-blob output decode. When the profile
                    // declares an extendedOutputReport with a matching
                    // reportId, decode the bytes into a parsed-field
                    // dictionary and surface as OutputDecoded. Consumers
                    // get named values (rumble amplitudes, lightbar RGB,
                    // adaptive-trigger blocks) instead of raw bytes.
                    var extOut = Profile.ExtendedOutputReport;
                    if (extOut != null && reportId == extOut.ReportIdByte
                        && OutputDecoded != null)
                    {
                        try
                        {
                            // Reconstruct the full report (RID + data) for
                            // the codec — VendorBlobCodec expects the RID
                            // at offset 0. The shared output ring stores
                            // the RID separately so we synthesize it here.
                            var full = new byte[dataSize + 1];
                            full[0] = reportId;
                            Buffer.BlockCopy(buf, 0, full, 1, dataSize);

                            var (fields, crcValid) = VendorBlobCodec.Decode(extOut, full);
                            OutputDecoded.Invoke(this, new GKOutputDecodedEventArgs
                            {
                                ReportId = reportId,
                                Fields = fields,
                                RawBytes = full,
                                CrcValid = crcValid,
                            });
                        }
                        catch
                        {
                            // Swallow decode errors so a malformed packet
                            // doesn't kill the polling thread. OutputReceived
                            // already fired with the raw bytes; consumers
                            // that need them have them.
                        }
                    }


                    // v1.3.5 — arm-handshake watcher. When the profile
                    // declares armOn triggers and a matching host action
                    // arrives, flip the armed flag — SubmitState then
                    // switches from legacy Report 0x01 emission to
                    // vendor-blob Report 0x31 / 0x11 emission via the
                    // extended shared-memory path (see SubmitState's
                    // useExtended branch). Sony BT profiles arm on
                    // Get_Feature 0x05 / 0x09 / 0x20 reads — the same
                    // handshake real Sony firmware uses to switch from
                    // basic to extended mode (ref: Linux hid-playstation
                    // dualsense_create init flow). featureWrite and
                    // outputWrite trigger types stay supported for
                    // future profiles that arm on writes (e.g. Switch
                    // Pro init handshake).
                    var extIn = Profile.ExtendedReport;
                    if (extIn?.ArmOn != null && !_extendedModeArmed)
                    {
                        bool isFeature     = source == (byte)GKOutputSource.HidFeature;
                        bool isOutput      = source == (byte)GKOutputSource.HidOutput;
                        bool isFeatureRead = source == (byte)GKOutputSource.HidFeatureRead;
                        foreach (var trig in extIn.ArmOn)
                        {
                            if ((trig.Type == "featureWrite" && isFeature     && trig.ReportIdByte == reportId)
                             || (trig.Type == "outputWrite"  && isOutput      && trig.ReportIdByte == reportId)
                             || (trig.Type == "featureRead"  && isFeatureRead && trig.ReportIdByte == reportId))
                            {
                                _extendedModeArmed = true;
                                break;
                            }
                        }
                    }

                    if (ct.IsCancellationRequested) break;
                }
            }
            catch
            {
                // Swallow polling errors so a transient kernel-side failure
                // doesn't kill the reader thread. An aborted drain may have
                // consumed a coalesced signal that covered a packet it never
                // reached, so the next wait must be short (see below).
                drainFaulted = true;
            }

            // Missed-signal evidence (audit of #34): data discovered by a
            // TIMEOUT wake means a producer published without a signal
            // reaching us. Two strikes before degrading: a packet landing
            // in the instant between timeout expiry and the drain leaves
            // its signal LATCHED (the next wait returns immediately), so a
            // single occurrence is a benign race, while a producer that
            // truly never signals accumulates strikes fast. On the second
            // strike, degrade event mode's timeout to the historical 8 ms
            // permanently; correctness beats idle savings.
            if (lastWakeWasTimeout && drainedAny && ++missedSignalStrikes >= 2)
                eventTimeoutMs = 8;

            // Event mode (issue #34): block on {cancel, doorbell} with a
            // safety timeout (500 ms while signals are proving reliable,
            // 8 ms after any missed-signal evidence, one 8 ms retry after
            // a faulted drain). The driver signals after publishing Head,
            // so an event wake always finds the packet. Poll fallback
            // (older driver, no event): the historical 8 ms CTS-handle
            // wait (T10), plus a throttled re-open attempt every 64 cycles
            // so a driver device that starts after this thread still
            // upgrades the loop to event mode.
            try
            {
                if (waitPair != null)
                {
                    int timeout = drainFaulted ? 8 : eventTimeoutMs;
                    drainFaulted = false;
                    int woke = WaitHandle.WaitAny(waitPair, timeout);
                    lastWakeWasTimeout = woke == WaitHandle.WaitTimeout;
                }
                else
                {
                    ct.WaitHandle.WaitOne(8);
                    if (--openRetryCountdown <= 0)
                    {
                        openRetryCountdown = 64;
                        IntPtr raw = SharedMemoryIO.TryOpenOutputEvent(Index);
                        if (raw != IntPtr.Zero)
                        {
                            doorbell = new AutoResetEvent(false);
                            doorbell.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(raw, ownsHandle: true);
                            waitPair = new WaitHandle[] { ct.WaitHandle, doorbell };
                        }
                    }
                }
            }
            catch { break; }
        }
        }
        finally
        {
            doorbell?.Dispose();
        }
    }

    private bool _disposed;
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GKController));
    }

    /// <summary>Removes the virtual device from PnP and frees the per-controller
    /// shared memory section. Idempotent — safe to call multiple times. Called
    /// automatically when the owning <see cref="GKContext"/> is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _outputCts.Cancel(); } catch { }
        try { _outputThread?.Join(Internal.TimeoutScale.Apply(500)); } catch { }
        // Drop the registration before the CTS is disposed, so a concurrent
        // unmap can never reach a disposed CTS through the registry (#45).
        // The thread is already cancelled and joined above, so this only
        // needs to forget it, not stop it again.
        try { Internal.SharedMemoryIO.UnregisterOutputPump(Index); } catch { }
        try { _outputCts.Dispose(); } catch { }
        _context.OnControllerDisposing(this);
    }
}
