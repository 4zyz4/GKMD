using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GKMD.Internal.Usbip;

/// <summary>One emulated composite USB device behind the USB/IP server
/// (issue #39). Owns the control-transfer state machine, the endpoint
/// dispatch, the pending-URB bookkeeping, and the bridges to the SDK's
/// existing per-controller shared-memory contract, so an
/// <see cref="GKMD.GKController"/> works over this backend without
/// a single change to its own code:
///
/// <list type="bullet">
/// <item>Input: a pump thread waits on Global\GKMDInputEvent&lt;N&gt;
/// (the same event <c>SubmitState</c> signals), seqlock-reads the input
/// section, and builds the wire report exactly as driver.c does: the
/// report is InputReportByteLength bytes, zero-filled, with
/// FirstInputReportId prepended on the legacy path and the extended
/// buffer passed through verbatim when ExtendedReportSize is set.</item>
/// <item>Output: interrupt-OUT transfers and SET_REPORT writes publish to
/// the Global\GKMDOutput&lt;N&gt; ring with driver.c's discipline
/// (reserve Head atomically, fill the slot, fence, publish slot.SeqNo,
/// doorbell last), so <c>GKController.OutputReceived</c> and
/// <c>OutputDecoded</c> fire as before.</item>
/// <item>Feature reads: the Sony Get_Feature table is driver.c's, byte for
/// byte (0x05/41 and 0x02/37-41 carrying the neutral calibration, 0x09/20
/// and 0x12/16 with the synthetic MAC for DS5 and DS4 respectively,
/// 0x20/64 and 0xA3/49 carrying real firmware info, 0x22/64 answering the
/// Bluetooth-patch read, VID 0x054C gated), including the HID_FEATURE_READ
/// ring notification that drives the extendedReport.armOn watcher.</item>
/// </list>
///
/// <para>PID FFB feature serving is deliberately absent here: no usbip
/// profile declares a PID block (they are Sony composite personas). A
/// future PID profile must port driver.c's PID Get/SetFeature handling
/// first.</para>
///
/// <para>Isochronous endpoints route to <see cref="UsbAudioEngine"/>;
/// completions come back on the pacing thread and are serialized onto the
/// connection by the server's send lock.</para></summary>
internal sealed class UsbipEmulatedDevice : IDisposable
{
    public UsbDescriptorSet Descriptors { get; }
    public UsbAudioEngine? Audio { get; }
    public string BusId { get; }

    /// <summary>Controller index, kept so the synthetic pairing MAC in
    /// BuildFeatureStub is stable and unique per controller (#43).</summary>
    private readonly int _index;
    public uint Devid { get; }

    private readonly HidReportBuilder? _builder;
    private readonly bool _hasHid;
    private readonly bool _switchProtocol;
    /// <summary>True for the Xbox One persona: the DATA section carries a
    /// 14-byte GIP input payload and <see cref="_gip"/> owns the wire
    /// protocol instead of the generic descriptor-driven encoder.</summary>
    private readonly bool _gipProtocol;
    /// <summary>True for the Xbox Series X|S persona (PID 0x0B12): the GIP
    /// metadata advertises the console-function-map / dynamic-latency
    /// interfaces and the shared payload is 15 bytes (14-byte body + Share).</summary>
    private readonly bool _gipSeries;
    private GipResponder? _gip;
    private readonly Queue<byte[]> _gipReplies = new();          // 0x02/0x03/0x04/0x07/0x01 replies, served before stream frames
    private uint _lastGipSharedSeqNo;
    private int _gipSendErrCount;
    /// <summary>The one interrupt-IN endpoint the input pump answers (the
    /// profile's HID input pipe, or 0x81 for the Xbox 360 vendor persona).
    /// Every other interrupt-IN endpoint stalls.</summary>
    private readonly byte _inputEndpoint;
    /// <summary>Switch device type reported to the host: 1 = Joy-Con (L),
    /// 2 = Joy-Con (R), 3 = Pro Controller. Sourced from the profile PID.</summary>
    private readonly byte _switchDeviceType;
    private readonly int _rawInputSize;
    private readonly GKMD.Internal.VendorControlRequest[]? _vendorRequests;
    // HID Enhanced Wheel Support: non-zero when the profile declares a
    // Resolution Multiplier feature (high-resolution mouse). The feature
    // report is report 0; GET_REPORT(Feature, 0) returns the current value
    // and SET_REPORT(Feature, 0) stores it.
    private readonly int _resolutionMultiplier;
    private byte _resolutionMultiplierValue;
    private readonly IntPtr _inputView;
    private readonly IntPtr _outputView;
    private readonly IntPtr _outputEvent;
    private IntPtr _inputWaitEvent;
    private readonly Thread _inputThread;
    private volatile bool _stop;

    // The connection's serialized sender; null until attached. Claimed
    // and released with CompareExchange so a racing second import can
    // never steal or clear another connection's ownership.
    private UsbipServer.Connection? _connection;

    private readonly object _hidLock = new();
    private readonly Queue<byte[]> _frameQueue = new();          // built wire reports awaiting a read
    private readonly Queue<uint> _pendingInterruptIn = new();    // seqnums of parked interrupt IN URBs
    private byte[] _lastInputReport;
    private uint _lastSharedSeqNo;

    // 鈹€鈹€ Switch Pro protocol responder state (VID 057E PID 2009) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
    private readonly object _switchLock = new();
    private readonly Queue<byte[]> _switchReplies = new();       // 0x81/0x21 replies, served before stream frames
    private readonly byte[] _switchBody = new byte[48];          // latest consumer-packed 0x30 body
    private volatile bool _switchImuEnabled;
    private int _switchTimerCounter;
    private uint _lastSwitchSharedSeqNo;
    private static readonly byte[] SwitchMacTemplate = { 0x98, 0xB6, 0xE9, 0x48, 0x4D, 0x30 };

    // Sony test command state machine
    private readonly SonyTestCommandHandler _testCmd = new();
    private const int ResponseBufferMaxLength = 256;
    private byte[] _testCmdResponse = new byte[ResponseBufferMaxLength];
    private int _testCmdResponseLength;
    private bool _testCmdReady;
    private int _testCmdPagesRemaining;
    private byte _testCmdTelemetryDeviceId;
    private byte _testCmdTelemetryActionId;
    private byte _testCmdStandardDeviceId;
    private byte _testCmdStandardActionId;

    // Device state.
    private volatile byte _configurationValue;

    private const int MaxFrameQueue = 8;

    public UsbipEmulatedDevice(ControllerProfile profile, int index)
    {
        _index = index;
        Descriptors = new UsbDescriptorSet(profile);
        BusId = $"1-{index + 1}";
        Devid = (1u << 16) | (uint)(index + 1);

        _hasHid = Descriptors.HasHidInterface;
        _inputEndpoint = Descriptors.InputEndpoint;
        // The Switch family (Pro 0x2009, Joy-Con L 0x2006, Joy-Con R 0x2007)
        // drives its own 0x30 wire format from the submitted body and never
        // uses the generic descriptor-driven encoder, so skip the (203-byte,
        // vendor-blob) report-descriptor parse entirely.
        _switchProtocol = Descriptors.VendorId == 0x057E &&
            (Descriptors.ProductId == 0x2009 || Descriptors.ProductId == 0x2006 ||
             Descriptors.ProductId == 0x2007);
        _switchDeviceType = Descriptors.ProductId == 0x2006 ? (byte)0x01
            : Descriptors.ProductId == 0x2007 ? (byte)0x02
            : (byte)0x03;
        _gipProtocol = !_switchProtocol && (profile.UsbConfiguration?.Gip ?? false);
        _gipSeries = _gipProtocol && (profile.UsbConfiguration?.GipSeries ?? false);
        _builder = (_hasHid && !_switchProtocol && !_gipProtocol) ? profile.GetOrBuildReportBuilder() : null;
        _rawInputSize = _builder != null && _builder.InputReportByteSize > 0
            ? _builder.InputReportByteSize
            : (profile.InputReportSize ?? 64);
        _vendorRequests = profile.UsbConfiguration?.VendorRequests?.ToArray();
        _resolutionMultiplier = profile.UsbConfiguration?.ResolutionMultiplier ?? 0;
        // Enumerating as high-resolution (logical 1 = physical max 120) avoids
        // depending on a host-side SET before the first scroll. A host that
        // writes the feature overwrites this.
        _resolutionMultiplierValue = _resolutionMultiplier > 0 ? (byte)1 : (byte)0;
        _lastInputReport = new byte[Math.Max(1, _rawInputSize)];
        if (_builder != null && _builder.InputReportId != 0) _lastInputReport[0] = _builder.InputReportId;
        if (_switchProtocol)
        {
            _lastInputReport[0] = 0x30;
            // Neutral 12-bit sticks: 0x800 packed little-nibble = 00 08 80.
            _switchBody[5] = 0x00; _switchBody[6] = 0x08; _switchBody[7] = 0x80;
            _switchBody[8] = 0x00; _switchBody[9] = 0x08; _switchBody[10] = 0x80;
        }

        // Sections and events first, so both this device and the
        // GKController constructed after it see the same objects.
        _inputView = SharedMemoryIO.EnsureInputMapping(index);
        _outputView = SharedMemoryIO.EnsureOutputMapping(index);
        _outputEvent = SharedMemoryIO.EnsureOutputEvent(index);
        _inputWaitEvent = SharedMemoryIO.OpenInputEventForWait(index);

        if (_gipProtocol)
        {
            _gip = new GipResponder((src, rid, data) => PublishOutput(src, rid, data), index, _gipSeries);
            GipLog.Write($"create index={index} vid={Descriptors.VendorId:X4} pid={Descriptors.ProductId:X4} series={_gipSeries}");
        }

        var cfg = profile.UsbConfiguration;
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
        Audio = hasAudioStream ? new UsbAudioEngine(profile, CompleteIsoOnWire) : null;

        _inputThread = new Thread(InputPumpLoop)
        {
            IsBackground = true,
            Name = $"GKUsbipInput_{index}",
            Priority = ThreadPriority.AboveNormal,
        };
        _inputThread.Start();
    }

    /// <summary>Claim this device for one connection. A second import
    /// while attached is refused ST_DEV_BUSY by the server.</summary>
    public bool TryClaimConnection(UsbipServer.Connection connection)
        => Interlocked.CompareExchange(ref _connection, connection, null) == null;

    public void DetachConnection(UsbipServer.Connection connection)
    {
        if (Interlocked.CompareExchange(ref _connection, null, connection) != connection)
            return; // a different connection owns the device
        Audio?.Clear();
        lock (_hidLock)
        {
            _pendingInterruptIn.Clear();
            _frameQueue.Clear();
            _switchReplies.Clear();
            _gipReplies.Clear();
        }
        if (_switchProtocol)
        {
            _switchImuEnabled = false;
            Interlocked.Exchange(ref _switchTimerCounter, 0);
            _lastSwitchSharedSeqNo = 0;
        }
        if (_gipProtocol)
        {
            _gip?.Reset();
            _lastGipSharedSeqNo = 0;
        }
    }

    // 鈹€鈹€ Input pump: shared section 鈫?interrupt IN 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void InputPumpLoop()
    {
        if (_gipProtocol)
        {
            GipPumpLoop();
            return;
        }
        if (_switchProtocol)
        {
            SwitchPumpLoop();
            return;
        }
        while (!_stop)
        {
            try
            {
                if (_inputWaitEvent != IntPtr.Zero)
                    WaitForSingleObject(_inputWaitEvent, 500);
                else
                    Thread.Sleep(4);
                if (_stop) break;

                if (!TryReadInputFrame(out var report)) continue;

                uint seq;
                lock (_hidLock)
                {
                    _lastInputReport = report;
                    if (_pendingInterruptIn.Count > 0)
                    {
                        seq = _pendingInterruptIn.Dequeue();
                    }
                    else
                    {
                        if (_frameQueue.Count >= MaxFrameQueue) _frameQueue.Dequeue();
                        _frameQueue.Enqueue(report);
                        continue;
                    }
                }
                SendInterruptInReply(seq, report);
            }
            catch
            {
                // Same containment contract as GKController.OutputPollLoop:
                // a transient failure must not kill the pump. The SDK's
                // per-frame SetEvent redelivers within one frame interval.
            }
        }
    }

    /// <summary>Seqlock read of the shared input section plus the wire
    /// report build, mirroring driver.c ReadSharedInput + the report
    /// assembly in its worker (driver.c:1240-1296).</summary>
    private bool TryReadInputFrame(out byte[] report)
    {
        report = Array.Empty<byte>();
        IntPtr view = _inputView;
        if (view == IntPtr.Zero) return false;

        Span<byte> snap = stackalloc byte[SharedMemoryIO.SHARED_INPUT_SIZE];
        uint seq1, seq2;
        int retries = 4;
        do
        {
            seq1 = (uint)Marshal.ReadInt32(view, 0);
            Thread.MemoryBarrier();
            unsafe
            {
                fixed (byte* dst = snap)
                    Buffer.MemoryCopy((void*)view, dst, snap.Length, snap.Length);
            }
            Thread.MemoryBarrier();
            seq2 = (uint)Marshal.ReadInt32(view, 0);
        } while ((seq1 != seq2 || (seq1 & 1) != 0) && --retries > 0);
        if (seq1 != seq2 || (seq1 & 1) != 0) return false;
        if (seq1 == _lastSharedSeqNo) return false; // no new frame
        _lastSharedSeqNo = seq1;

        int extSize = BitConverter.ToInt32(snap[SharedMemoryIO.EXTENDED_SIZE_OFFSET..]);
        if (extSize > 0 && extSize <= SharedMemoryIO.EXTENDED_DATA_CAPACITY)
        {
            report = snap.Slice(SharedMemoryIO.EXTENDED_DATA_OFFSET, extSize).ToArray();
            return true;
        }

        // Vendor-class (non-HID) interfaces carry an opaque raw input report
        // with no Report ID. Transmit exactly _rawInputSize bytes from the
        // DATA section verbatim (e.g. the 20-byte Xbox 360 wired report).
        if (!_hasHid)
        {
            int size = Math.Min(_rawInputSize, SharedMemoryIO.DATA_CAPACITY);
            var rv = new byte[size];
            snap.Slice(SharedMemoryIO.DATA_OFFSET, size).CopyTo(rv);
            report = rv;
            return true;
        }

        int expectedSize = _builder!.InputReportByteSize > 0 ? _builder.InputReportByteSize : 17;
        int dataLen = BitConverter.ToInt32(snap[4..]);
        bool hasReportId = _builder.InputReportId != 0;
        int maxData = hasReportId ? Math.Max(expectedSize - 1, 16) : expectedSize;
        if (hasReportId && expectedSize > 1) maxData = expectedSize - 1;
        if (dataLen > maxData) dataLen = maxData;
        if (dataLen > SharedMemoryIO.DATA_CAPACITY) dataLen = SharedMemoryIO.DATA_CAPACITY;
        if (dataLen < 0) dataLen = 0;

        var r = new byte[expectedSize];
        if (hasReportId)
        {
            r[0] = _builder.InputReportId;
            snap.Slice(SharedMemoryIO.DATA_OFFSET, dataLen).CopyTo(r.AsSpan(1));
        }
        else
        {
            snap.Slice(SharedMemoryIO.DATA_OFFSET, dataLen).CopyTo(r);
        }
        report = r;
        return true;
    }

    // 鈹€鈹€ Xbox One GIP responder (045E:02EA) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
    //
    // The vendor-class Xbox One persona speaks Microsoft's Gaming Input
    // Protocol instead of HID. The consumer packs the 14-byte
    // gip_gamepad_pkt_input body (XboxOneGipInput layout: buttons, triggers,
    // sticks) into the shared DATA section via SubmitRawReport; this side owns
    // the GIP header/sequence framing, the announce/identify/status handshake
    // and the guide virtual-key report. The host's power/LED/rumble/identify
    // commands arrive on the interrupt OUT endpoint and are decoded by
    // GipResponder. See GipResponder / GipProtocol and xone's bus/protocol.c.

    private void GipPumpLoop()
    {
        if (_gip == null) return;

        SendGipFrame(_gip.EncodeHello(), control: true);
        GipLog.Write("gip pump: sent initial HELLO (0x02)");
        long lastAnnounce = Environment.TickCount64;
        long lastStatus = 0;
        long lastInputSend = 0;
        bool loggedActive = false;

        while (!_stop)
        {
            try
            {
                if (_inputWaitEvent != IntPtr.Zero)
                    WaitForSingleObject(_inputWaitEvent, 8);
                else
                    Thread.Sleep(8);
                if (_stop) break;

                long now = Environment.TickCount64;

                // Keep announcing until the host answers; a booting host may
                // enumerate before its GIP stack is ready to read.
                if (!_gip.HostSeen && now - lastAnnounce >= 500)
                {
                    SendGipFrame(_gip.EncodeHello(), control: true);
                    lastAnnounce = now;
                }

                bool changed = false;
                if (TryReadGipPayload(out var body))
                    changed = _gip.UpdatePayload(body);

                if (_gip.Active && !loggedActive)
                {
                    loggedActive = true;
                    GipLog.Write("gip pump: host seen -> streaming INPUT");
                }

                if (_gip.Active)
                {
                    if (_gip.TakeGuideChange(out bool down))
                        SendGipFrame(_gip.EncodeGuide(down), control: true);

                    // Spec: input reports go out only when the payload changed,
                    // plus a slow keep-alive so a parked URB is still serviced.
                    if (changed || now - lastInputSend >= 250)
                    {
                        SendGipFrame(_gip.BuildInputFrame(), control: false);
                        lastInputSend = now;
                    }

                    if (now - lastStatus >= 1000)
                    {
                        SendGipFrame(_gip.EncodeStatus(), control: true);
                        lastStatus = now;
                    }
                }
            }
            catch
            {
                // Same containment contract as the other pumps: a transient
                // failure must not kill the thread.
            }
        }
    }

    /// <summary>Seqlock read of the GIP input payload from the shared DATA
    /// section: 14 bytes for the Xbox One, 15 for the Series (body + Share).
    /// Keeps the last body when no new frame has arrived.</summary>
    private bool TryReadGipPayload(out byte[] body)
    {
        body = Array.Empty<byte>();
        IntPtr view = _inputView;
        if (view == IntPtr.Zero) return false;

        Span<byte> snap = stackalloc byte[SharedMemoryIO.SHARED_INPUT_SIZE];
        uint seq1, seq2;
        int retries = 4;
        do
        {
            seq1 = (uint)Marshal.ReadInt32(view, 0);
            Thread.MemoryBarrier();
            unsafe
            {
                fixed (byte* dst = snap)
                    Buffer.MemoryCopy((void*)view, dst, snap.Length, snap.Length);
            }
            Thread.MemoryBarrier();
            seq2 = (uint)Marshal.ReadInt32(view, 0);
        } while ((seq1 != seq2 || (seq1 & 1) != 0) && --retries > 0);
        if (seq1 != seq2 || (seq1 & 1) != 0) return false;
        if (seq1 == _lastGipSharedSeqNo) return false;
        _lastGipSharedSeqNo = seq1;

        int dataLen = BitConverter.ToInt32(snap[4..]);
        // Series adds a trailing console-function-map byte (Share) after the
        // 14-byte gamepad body.
        int cap = _gipSeries ? 15 : 14;
        if (dataLen > cap) dataLen = cap;
        if (dataLen <= 0) return false;
        var b = new byte[cap];
        snap.Slice(SharedMemoryIO.DATA_OFFSET, dataLen).CopyTo(b);
        body = b;
        return true;
    }

    /// <summary>Completes a parked interrupt-IN URB with <paramref name="frame"/>
    /// immediately, or queues it. Control replies (announce/identify/status/
    /// guide/ack) preempt stream frames in <see cref="_gipReplies"/>.</summary>
    private void SendGipFrame(byte[] frame, bool control)
    {
        uint seq;
        bool have = false;
        lock (_hidLock)
        {
            if (_pendingInterruptIn.Count > 0)
            {
                seq = _pendingInterruptIn.Dequeue();
                have = true;
            }
            else
            {
                var q = control ? _gipReplies : _frameQueue;
                if (q.Count >= MaxFrameQueue) q.Dequeue();
                q.Enqueue(frame);
                seq = 0;
            }
        }
        if (have) SendInterruptInReply(seq, frame);
    }

    private void HandleGipOutput(ReadOnlySpan<byte> payload)
    {
        _gip?.HandleHostBuffer(payload, f => SendGipFrame(f, control: true));
    }

    // 鈹€鈹€ Switch Pro protocol responder (057E:2009) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
    //
    // Port of driver.c's SwitchHandle* family (GKMD issue #33) onto the
    // USB/IP transport. The consumer packs the 48-byte report-0x30 body
    // (SwitchProPacker layout: [0]counter [1]battery [2..4]buttons [5..10]
    // sticks [11]vibrator [12..47]IMU x3) into the shared DATA section via
    // SubmitRawReport. This side owns the wire framing, the request/reply
    // state machine and the fabricated SPI image, and streams report 0x30 at
    // the real 15 ms cadence so SDL's HIDAPI_DriverSwitch completes its USB
    // init (0x80 handshake, 0x01 subcommands) and eden sees a genuine pad.

    private const byte SwitchBatteryConn = 0x91; // full + charging, wired
    private const byte SwitchVibrator = 0xB0;

    private void SwitchPumpLoop()
    {
        while (!_stop)
        {
            try
            {
                if (_inputWaitEvent != IntPtr.Zero)
                    WaitForSingleObject(_inputWaitEvent, 15);
                else
                    Thread.Sleep(15);
                if (_stop) break;

                TryReadSwitchBody();

                var frame = BuildSwitchFrame();
                uint seq;
                bool have = false;
                lock (_hidLock)
                {
                    _lastInputReport = frame;
                    if (_pendingInterruptIn.Count > 0)
                    {
                        seq = _pendingInterruptIn.Dequeue();
                        have = true;
                    }
                    else
                    {
                        if (_frameQueue.Count >= MaxFrameQueue) _frameQueue.Dequeue();
                        _frameQueue.Enqueue(frame);
                        seq = 0;
                    }
                }
                if (have) SendInterruptInReply(seq, frame);
            }
            catch
            {
                // Same containment contract as the generic input pump.
            }
        }
    }

    /// <summary>Seqlock read of the 48-byte Switch body from the shared DATA
    /// section. Keeps the last body when no new frame has arrived so the
    /// streamer always has a full state to send.</summary>
    private void TryReadSwitchBody()
    {
        IntPtr view = _inputView;
        if (view == IntPtr.Zero) return;

        Span<byte> snap = stackalloc byte[SharedMemoryIO.SHARED_INPUT_SIZE];
        uint seq1, seq2;
        int retries = 4;
        do
        {
            seq1 = (uint)Marshal.ReadInt32(view, 0);
            Thread.MemoryBarrier();
            unsafe
            {
                fixed (byte* dst = snap)
                    Buffer.MemoryCopy((void*)view, dst, snap.Length, snap.Length);
            }
            Thread.MemoryBarrier();
            seq2 = (uint)Marshal.ReadInt32(view, 0);
        } while ((seq1 != seq2 || (seq1 & 1) != 0) && --retries > 0);
        if (seq1 != seq2 || (seq1 & 1) != 0) return;
        if (seq1 == _lastSwitchSharedSeqNo) return;
        _lastSwitchSharedSeqNo = seq1;

        int dataLen = BitConverter.ToInt32(snap[4..]);
        if (dataLen > _switchBody.Length) dataLen = _switchBody.Length;
        if (dataLen < 0) dataLen = 0;
        if (dataLen == 0) return;
        lock (_switchLock)
            snap.Slice(SharedMemoryIO.DATA_OFFSET, dataLen).CopyTo(_switchBody);
    }

    /// <summary>Copy the latest buttons/sticks + (when enabled) IMU out of the
    /// consumer body into the 46-byte state layout the 0x30/0x21 frames embed
    /// ([0..9) buttons+sticks, [10..46) IMU x3).</summary>
    private void FillSwitchState(Span<byte> state)
    {
        state.Clear();
        lock (_switchLock)
        {
            _switchBody.AsSpan(2, 9).CopyTo(state);
            if (_switchImuEnabled) _switchBody.AsSpan(12, 36).CopyTo(state.Slice(10, 36));
        }
    }

    /// <summary>Build one 64-byte report-0x30 full-mode frame from the latest
    /// body, with the timer/battery/vibrator bytes overlaid (driver.c
    /// SwitchStreamProc).</summary>
    private byte[] BuildSwitchFrame()
    {
        var state = new byte[46];
        FillSwitchState(state);

        var frame = new byte[64];
        frame[0] = 0x30;
        frame[1] = (byte)Interlocked.Increment(ref _switchTimerCounter);
        frame[2] = SwitchBatteryConn;
        state.AsSpan(0, 9).CopyTo(frame.AsSpan(3));
        frame[12] = SwitchVibrator;
        state.AsSpan(10, 36).CopyTo(frame.AsSpan(13));
        return frame;
    }

    /// <summary>Queue a synthesized reply frame (0x81/0x21). Completes a parked
    /// interrupt IN immediately when one exists; otherwise parks it ahead of
    /// the stream frames in <see cref="_switchReplies"/>.</summary>
    private void SwitchQueueReply(byte[] reply)
    {
        uint seq;
        bool have = false;
        lock (_hidLock)
        {
            if (_pendingInterruptIn.Count > 0)
            {
                seq = _pendingInterruptIn.Dequeue();
                have = true;
            }
            else
            {
                if (_switchReplies.Count >= MaxFrameQueue) _switchReplies.Dequeue();
                _switchReplies.Enqueue(reply);
                seq = 0;
            }
        }
        if (have) SendInterruptInReply(seq, reply);
    }

    /// <summary>Output report 0x80 USB init commands (dekuNukem USB-HID-Notes):
    /// status, handshake, high-speed, ForceUSB. Payload here is the bytes
    /// after the report ID (payload[0] = proprietary command id).</summary>
    private void SwitchHandleProprietary(ReadOnlySpan<byte> payload)
    {
        byte cmd = payload.Length > 0 ? payload[0] : (byte)0;

        var reply = new byte[64];
        reply[0] = 0x81;
        reply[1] = cmd;
        switch (cmd)
        {
            case 0x01: // Status: 81 01 00 <type> <MAC reversed>
                reply[2] = 0x00;
                reply[3] = _switchDeviceType;
                for (int i = 0; i < 6; i++) reply[4 + i] = SwitchMac(5 - i);
                SwitchQueueReply(reply);
                break;
            case 0x02: // Handshake ack (load-bearing for BTrySetupUSB)
            case 0x03: // Baud-switch / high-speed ack
                SwitchQueueReply(reply);
                break;
            default:   // ForceUSB / ClearUSB / ResetMCU have no defined reply
                break;
        }
    }

    /// <summary>Output report 0x01 rumble + subcommand -> input report 0x21.
    /// Payload is the bytes after the report ID: [counter, rumble L x4,
    /// rumble R x4, subcommand, args...].</summary>
    private void SwitchHandleSubcommand(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10) return;

        byte subcmd = payload[9];
        var args = payload[10..];

        var state = new byte[46];
        FillSwitchState(state);

        var reply = new byte[64];
        reply[0] = 0x21;
        reply[1] = (byte)Interlocked.Increment(ref _switchTimerCounter);
        reply[2] = SwitchBatteryConn;
        state.AsSpan(0, 9).CopyTo(reply.AsSpan(3));
        reply[12] = SwitchVibrator;
        reply[13] = 0x80; // generic ACK default
        reply[14] = subcmd;

        switch (subcmd)
        {
            case 0x02: // Request device info
                reply[13] = 0x82;
                reply[15] = 0x03; // firmware 03.8B
                reply[16] = 0x8B;
                reply[17] = _switchDeviceType; // 1=L, 2=R, 3=Pro
                reply[18] = 0x02;
                for (int i = 0; i < 6; i++) reply[19 + i] = SwitchMac(i);
                reply[25] = 0x01;
                reply[26] = 0x01; // colors live in SPI
                break;

            case 0x03: // Set input report mode
                if (args.Length >= 1 &&
                    (args[0] == 0x30 || args[0] == 0x31 || args[0] == 0x3F))
                {
                    // The USB descriptor only declares the 0x30 full-mode report,
                    // so the streamer keeps emitting 0x30 regardless of the
                    // requested mode (accepted with a generic ACK).
                }
                break;

            case 0x04: // Trigger buttons elapsed time
                reply[13] = 0x83;
                break;

            case 0x10: // SPI flash read: echo address+length, serve the image
                if (args.Length < 5) break;
                uint addr = (uint)(args[0] | (args[1] << 8) | (args[2] << 16) | (args[3] << 24));
                int len = args[4];
                if (len > 0x1D) len = 0x1D;
                reply[13] = 0x90;
                args.Slice(0, 5).CopyTo(reply.AsSpan(15));
                for (int i = 0; i < len; i++) reply[20 + i] = SwitchSpiByte(addr + (uint)i);
                break;

            case 0x21: // Set NFC/IR MCU config
                reply[13] = 0xA0;
                reply[15] = 0x01; reply[16] = 0x00; reply[17] = 0xFF;
                reply[18] = 0x00; reply[19] = 0x08; reply[20] = 0x00;
                reply[21] = 0x1B; reply[22] = 0x01;
                reply[48] = 0xC8;
                break;

            case 0x40: // Enable/disable IMU streaming
                if (args.Length >= 1) _switchImuEnabled = args[0] != 0;
                break;

            case 0x48: // Enable vibration
                reply[13] = 0x82;
                break;

            default: // Unknown: generic ACK, never NACK
                break;
        }

        SwitchQueueReply(reply);
    }

    private byte SwitchMac(int i)
        => i == 5 ? (byte)(SwitchMacTemplate[5] + _index) : SwitchMacTemplate[i];

    /// <summary>Fabricated SPI flash image, served byte-wise so any
    /// (address, length) read gets a consistent answer. Mirrors the image
    /// driver.c serves: IMU factory calibration @0x6020 (zero origins,
    /// coefficients that reduce SDL's LoadIMUCalibration to its default
    /// scales), stick factory calibration @0x603D (center 0x800, range
    /// 0x600), colors @0x6050, and the six-axis/stick params blocks. Also
    /// serves a serial number @0x6000 and device type @0x6012 (1=L, 2=R,
    /// 3=Pro) that driver.c leaves 0xFF, which eden's joycon driver reads on
    /// init.</summary>
    private byte SwitchSpiByte(uint a)
    {
        ReadOnlySpan<byte> imuCal = stackalloc byte[24]
        {
            0x00,0x00, 0x00,0x00, 0x00,0x00,
            0x00,0x40, 0x00,0x40, 0x00,0x40,
            0x00,0x00, 0x00,0x00, 0x00,0x00,
            0x3B,0x34, 0x3B,0x34, 0x3B,0x34,
        };
        ReadOnlySpan<byte> stickCal = stackalloc byte[18]
        {
            0x00,0x06,0x60, 0x00,0x08,0x80, 0x00,0x06,0x60,
            0x00,0x08,0x80, 0x00,0x06,0x60, 0x00,0x06,0x60,
        };
        ReadOnlySpan<byte> colors = stackalloc byte[12]
        {
            0x32,0x32,0x32, 0xFF,0xFF,0xFF, 0xFF,0xFF,0xFF, 0xFF,0xFF,0xFF,
        };
        ReadOnlySpan<byte> sixAxisParams = stackalloc byte[6] { 0x50,0xFD,0x00,0x00,0xC6,0x0F };
        ReadOnlySpan<byte> stickParams = stackalloc byte[18]
        {
            0x0F,0x30,0x61, 0x00,0x30,0xF3, 0xD4,0x14,0x54,
            0x41,0x15,0x54, 0xC7,0x79,0x9C, 0x33,0x36,0x63,
        };
        // Serial number @0x6000 (16 bytes). dekuNukem: byte 0 is a leading
        // 0x00, then 15 ASCII characters; eden copies from buffer+1. Real
        // Switch serials are 14 chars + NUL, so the tail past "XAW1..." is
        // zero-padded.
        ReadOnlySpan<byte> serial = stackalloc byte[16]
        {
            0x00,
            0x58,0x41,0x57,0x31,0x30,0x30,0x30,0x30,
            0x30,0x30,0x30,0x30,0x30,0x30,0x00,
        };

        if (a >= 0x6000 && a < 0x6000 + 16) return serial[(int)(a - 0x6000)];
        if (a == 0x6012) return _switchDeviceType; // 1=L, 2=R, 3=Pro
        if (a >= 0x6020 && a < 0x6020 + 24) return imuCal[(int)(a - 0x6020)];
        if (a >= 0x603D && a < 0x603D + 18) return stickCal[(int)(a - 0x603D)];
        if (a >= 0x6050 && a < 0x6050 + 12) return colors[(int)(a - 0x6050)];
        if (a >= 0x6080 && a < 0x6080 + 6)  return sixAxisParams[(int)(a - 0x6080)];
        if (a >= 0x6086 && a < 0x6086 + 18) return stickParams[(int)(a - 0x6086)];
        if (a >= 0x6098 && a < 0x6098 + 18) return stickParams[(int)(a - 0x6098)];
        return 0xFF;
    }

    /// <summary>Intercept a Switch output report. 0x80 is pure protocol and
    /// never published; 0x01 (rumble+subcommand) and 0x10 (rumble only) are
    /// published raw so GamepadSession can decode HD rumble into LRA audio.</summary>
    private void HandleSwitchOutput(byte reportId, ReadOnlySpan<byte> data)
    {
        if (reportId == 0x80)
        {
            SwitchHandleProprietary(data);
            return;
        }
        if (reportId == 0x01) SwitchHandleSubcommand(data);
        PublishOutput(SourceHidOutput, reportId, data);
    }

    // 鈹€鈹€ Output ring publish (driver.c PublishOutput discipline) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    public const byte SourceHidOutput = 0;       // driver.h GKMD_OUTPUT_SOURCE_*
    public const byte SourceHidFeature = 1;
    public const byte SourceHidFeatureRead = 3;

    private readonly object _publishLock = new();

    private void PublishOutput(byte source, byte reportId, ReadOnlySpan<byte> data)
    {
        IntPtr view = _outputView;
        if (view == IntPtr.Zero) return;
        int size = Math.Min(data.Length, SharedMemoryIO.DATA_CAPACITY);
        lock (_publishLock)
        {
            uint newSeq;
            unsafe { newSeq = (uint)Interlocked.Increment(ref *(int*)view); }
            int slotIdx = (int)((newSeq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS);
            int slotBase = SharedMemoryIO.OUTPUT_HEADER_SIZE + slotIdx * SharedMemoryIO.OUTPUT_SLOT_SIZE;
            Marshal.WriteByte(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, source);
            Marshal.WriteByte(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, reportId);
            Marshal.WriteInt16(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, (short)size);
            if (size > 0)
            {
                unsafe
                {
                    fixed (byte* src = data)
                        Buffer.MemoryCopy(src, (byte*)view + slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA,
                            SharedMemoryIO.DATA_CAPACITY, size);
                }
            }
            Thread.MemoryBarrier();
            Marshal.WriteInt32(view, slotBase + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)newSeq);
        }
        if (_outputEvent != IntPtr.Zero) SetEvent(_outputEvent);
    }

    // 鈹€鈹€ URB dispatch (called on the connection reader thread) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    public void HandleSubmit(in UsbipProtocol.CommandHeader cmd, byte[]? outPayload,
                             (uint Offset, uint Length)[]? isoPackets)
    {
        if (cmd.Ep == 0)
        {
            HandleControl(cmd, outPayload);
            return;
        }

        byte epAddr = (byte)(cmd.Ep | (cmd.IsIn ? 0x80u : 0u));
        if (!Descriptors.Endpoints.TryGetValue(epAddr, out var ep))
        {
            SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            return;
        }

        if (ep.TransferType == 1) // isochronous 鈫?audio engine
        {
            if (Audio == null)
            {
                SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
                return;
            }
            var urb = new UsbAudioEngine.PendingIso
            {
                Seqnum = cmd.Seqnum,
                IsIn = cmd.IsIn,
                TransferBufferLength = cmd.TransferBufferLength,
                Packets = isoPackets ?? Array.Empty<(uint, uint)>(),
                OutPayload = outPayload,
            };
            Audio.SubmitIso(urb);
            return;
        }

        if (ep.TransferType == 3) // interrupt
        {
            if (cmd.IsIn)
            {
                // Only the profile's designated input endpoint carries the
                // input stream. Auxiliary interrupt-IN endpoints (the Xbox
                // 360 headset's 0x84, the controller's 0x82/0x83) must stall:
                // answering them with gamepad bytes makes xusb22 believe a
                // headset is attached and synthesize a phantom USB Audio
                // function (mono, 8 kHz) that then binds usbaudio.
                if (epAddr != _inputEndpoint)
                {
                    SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
                    return;
                }

                byte[]? frame = null;
                lock (_hidLock)
                {
                    // Protocol replies (GIP 0x02/0x03/0x04/0x07, Switch
                    // 0x81/0x21) preempt stream frames.
                    if (_gipProtocol && _gipReplies.Count > 0) frame = _gipReplies.Dequeue();
                    else if (_switchProtocol && _switchReplies.Count > 0) frame = _switchReplies.Dequeue();
                    else if (_frameQueue.Count > 0) frame = _frameQueue.Dequeue();
                    else
                    {
                        _pendingInterruptIn.Enqueue(cmd.Seqnum);
                    }
                }
                if (frame != null) SendInterruptInReply(cmd.Seqnum, frame);
                return;
            }

            // Interrupt OUT: a full output report, Report ID first when the
            // descriptor declares IDs. Same split as driver.c WRITE_REPORT.
            var payload = outPayload ?? Array.Empty<byte>();
            byte rid = payload.Length > 0 ? payload[0] : (byte)0;
            var outData = payload.Length > 0 ? payload.AsSpan(1) : ReadOnlySpan<byte>.Empty;
            if (_gipProtocol)
                HandleGipOutput(payload); // GIP frames are not report-ID prefixed
            else if (_switchProtocol && (rid == 0x80 || rid == 0x01 || rid == 0x10))
                HandleSwitchOutput(rid, outData);
            else
                PublishOutput(SourceHidOutput, rid, outData);
            SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
            return;
        }

        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    public void HandleUnlink(uint seqnum, uint victimSeqnum)
    {
        bool removed = false;
        if (Audio != null)
            removed = Audio.TryUnlink(victimSeqnum);
        if (!removed)
        {
            lock (_hidLock)
            {
                // Queue<T> has no random removal; rebuild without the victim.
                if (_pendingInterruptIn.Contains(victimSeqnum))
                {
                    var keep = new List<uint>(_pendingInterruptIn.Count);
                    while (_pendingInterruptIn.Count > 0)
                    {
                        uint s = _pendingInterruptIn.Dequeue();
                        if (s != victimSeqnum) keep.Add(s);
                        else removed = true;
                    }
                    foreach (var s in keep) _pendingInterruptIn.Enqueue(s);
                }
            }
        }
        // Protocol rule (usbip-win2 wsk_receive.cpp cites usbip_protocol.rst):
        // -ECONNRESET when the URB was still queued, 0 when already answered.
        var conn = _connection;
        if (conn == null) return;
        try { conn.SendRetUnlink(seqnum, removed ? -UsbipProtocol.EConnReset : 0); } catch { }
    }

    // 鈹€鈹€ Control transfers 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void HandleControl(in UsbipProtocol.CommandHeader cmd, byte[]? outPayload)
    {
        Span<byte> setup = stackalloc byte[8];
        BitConverter.TryWriteBytes(setup, cmd.Setup);
        byte bmRequestType = setup[0];
        byte bRequest = setup[1];
        ushort wValue = (ushort)(setup[2] | (setup[3] << 8));
        ushort wIndex = (ushort)(setup[4] | (setup[5] << 8));
        ushort wLength = (ushort)(setup[6] | (setup[7] << 8));

        byte type = (byte)((bmRequestType >> 5) & 0x03);      // 0 standard, 1 class, 2 vendor
        byte recipient = (byte)(bmRequestType & 0x1F);        // 0 device, 1 interface, 2 endpoint, 3 other
        bool deviceToHost = (bmRequestType & 0x80) != 0;

        if (_gipProtocol)
            GipLog.Write($"CTRL rt=0x{bmRequestType:X2} req=0x{bRequest:X2} " +
                         $"val=0x{wValue:X4} idx=0x{wIndex:X4} len={wLength} type={type} rcp={recipient} in={deviceToHost}");

        // Hub port reset arrives as USB_RT_PORT SET_FEATURE(PORT_RESET)
        // (usbip-win2 device_ioctl.cpp make_reset_port: "meaningless for a
        // server which ignores it"). Reset to the unconfigured state.
        if (bmRequestType == 0x23 && bRequest == 0x03)
        {
            ResetDeviceState();
            SendRetSubmit(cmd.Seqnum, 0, 0, null);
            return;
        }

        if (type == 0) // standard
        {
            switch (bRequest)
            {
                case 0x06: // GET_DESCRIPTOR
                {
                    byte descType = (byte)(wValue >> 8);
                    byte descIndex = (byte)(wValue & 0xFF);
                    byte[]? d;
                    if (recipient == 1)
                    {
                        // Class descriptor on an interface: 0x22 is the HID
                        // report descriptor (HID interface only); 0x21 is the
                        // HID class descriptor or a vendor 0x21 blob, keyed by
                        // the interface number.
                        d = descType == 0x22 && Descriptors.HasHidInterface
                            ? Descriptors.ReportDescriptor
                            : Descriptors.GetClassDescriptor((byte)(wIndex & 0xFF));
                    }
                    else
                    {
                        d = Descriptors.GetDescriptor(descType, descIndex, wIndex);
                    }
                    if (d == null) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                    int n = Math.Min(d.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, d.AsSpan(0, n).ToArray());
                    return;
                }
                case 0x00: // GET_STATUS
                {
                    if (!deviceToHost) break;
                    // Device: self-powered bit from bmAttributes 0xC0. Interface/endpoint: zero.
                    ushort status = recipient == 0 ? (ushort)0x0001 : (ushort)0x0000;
                    var d = new[] { (byte)(status & 0xFF), (byte)(status >> 8) };
                    int n = Math.Min(d.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, d.AsSpan(0, n).ToArray());
                    return;
                }
                case 0x09: // SET_CONFIGURATION
                    _configurationValue = (byte)(wValue & 0xFF);
                    ResetAltSettings();
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x08: // GET_CONFIGURATION
                {
                    var d = new[] { _configurationValue };
                    int n = Math.Min(1, (int)wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, n > 0 ? d : null);
                    return;
                }
                case 0x0B: // SET_INTERFACE
                    Audio?.SetAltSetting((byte)(wIndex & 0xFF), (byte)(wValue & 0xFF));
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x0A: // GET_INTERFACE
                {
                    byte alt = Audio?.GetAltSetting((byte)(wIndex & 0xFF)) ?? (byte)0;
                    var d = new[] { alt };
                    SendRetSubmit(cmd.Seqnum, 0, Math.Min(1, (int)wLength),
                        wLength > 0 ? d : null);
                    return;
                }
                case 0x01: // CLEAR_FEATURE (ENDPOINT_HALT arrives via ude clear_endpoint_stall)
                case 0x03: // SET_FEATURE
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
                case 0x05: // SET_ADDRESS (UDE normally handles this itself)
                    SendRetSubmit(cmd.Seqnum, 0, 0, null);
                    return;
            }
            SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            return;
        }

        if (type == 1 && recipient == 1) // class, interface recipient
        {
            byte ifaceNum = (byte)(wIndex & 0xFF);
            if (ifaceNum == Descriptors.HidInterfaceNumber)
            {
                HandleHidClassRequest(cmd, bRequest, wValue, wLength, deviceToHost, outPayload);
                return;
            }

            // UAC1 feature-unit request: wIndex high byte is the entity.
            byte unitId = (byte)(wIndex >> 8);
            byte selector = (byte)(wValue >> 8);
            byte channel = (byte)(wValue & 0xFF);
            if (Audio != null)
            {
                if (deviceToHost)
                {
                    var d = Audio.HandleUacGet(bRequest, unitId, selector, channel, wLength);
                    if (d == null) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                    int n = Math.Min(d.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, d.AsSpan(0, n).ToArray());
                }
                else
                {
                    bool ok = bRequest == 0x01 // SET_CUR
                        && Audio.HandleUacSet(unitId, selector, channel,
                            outPayload ?? Array.Empty<byte>());
                    if (ok) SendRetSubmit(cmd.Seqnum, 0, outPayload?.Length ?? 0, null);
                    else SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
                }
                return;
            }
            return;
        }

        if (type == 1 && recipient == 2) // class, endpoint recipient: UAC sampling frequency
        {
            // The high-speed blob advertises no endpoint controls
            // (AS iso endpoint bmAttributes 0x00), so usbaudio does not
            // send these on the operating speed. Accept SET_CUR / answer
            // GET_CUR for SAMPLING_FREQ anyway: the device has exactly one
            // discrete rate and refusing a redundant set would fail a host
            // that sends it regardless.
            byte selector = (byte)(wValue >> 8);
            if (selector == 0x01)
            {
                if (deviceToHost)
                {
                    var freq = new byte[] { 0x80, 0xBB, 0x00 }; // 48000, 3-byte UAC1 rate
                    int n = Math.Min(freq.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, freq.AsSpan(0, n).ToArray());
                }
                else
                {
                    SendRetSubmit(cmd.Seqnum, 0, outPayload?.Length ?? 0, null);
                }
                return;
            }
            SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
            return;
        }

        // Vendor-class (type 2) requests to an interface: serve the
        // profile's vendorRequests table. This is the extensibility seam
        // for non-HID controllers (Xbox 360 capability report, etc.).
        if (type == 2 && recipient == 1 && _vendorRequests != null)
        {
            foreach (var r in _vendorRequests)
            {
                if (r.RequestType != bmRequestType || r.Request != bRequest
                    || r.Value != wValue || r.Index != wIndex)
                    continue;
                if (deviceToHost)
                {
                    if (r.Response == null) break;
                    var bytes = Convert.FromHexString(
                        r.Response.Replace(" ", "").Replace("-", ""));
                    int n = Math.Min(bytes.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, bytes.AsSpan(0, n).ToArray());
                }
                else
                {
                    PublishOutput(SourceHidFeature, 0, outPayload ?? ReadOnlySpan<byte>.Empty);
                    SendRetSubmit(cmd.Seqnum, 0, outPayload?.Length ?? 0, null);
                }
                return;
            }
        }

        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    private void HandleHidClassRequest(in UsbipProtocol.CommandHeader cmd, byte bRequest,
        ushort wValue, ushort wLength, bool deviceToHost, byte[]? outPayload)
    {
        byte reportType = (byte)(wValue >> 8); // 1 input, 2 output, 3 feature
        byte reportId = (byte)(wValue & 0xFF);

        switch (bRequest)
        {
            case 0x01 when deviceToHost: // GET_REPORT
            {
                // High-resolution wheel handshake: the Resolution Multiplier
                // feature lives in report 0 (the mouse descriptor uses no
                // report IDs).
                if (_resolutionMultiplier > 0 && reportType == 0x03 && reportId == 0)
                {
                    var d = new[] { _resolutionMultiplierValue };
                    int n = Math.Min(1, (int)wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, n > 0 ? d : null);
                    return;
                }
                if (reportType == 0x01)
                {
                    byte[] snapshot;
                    lock (_hidLock) snapshot = _lastInputReport;
                    int n = Math.Min(snapshot.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, n, snapshot.AsSpan(0, n).ToArray());
                    return;
                }
                if (reportType == 0x03)
                {
                    // Sony DualSense test command response (Report ID 0x81)
                    if (_testCmdReady)
                    {
                        byte[] response;
                        int responseLen;

                        if (_testCmdPagesRemaining >= 0)
                        {
                            // Telemetry multi-page response (injects 0x81 header dynamically)
                            if (_testCmdResponseLength < 0)
                            {
                                // _testCmdResponseLength = -(totalPages), _testCmdPagesRemaining = remaining pages including current
                                int totalPages = -_testCmdResponseLength;
                                int currentPage = totalPages - _testCmdPagesRemaining; // 0-based
                                int offset = currentPage * 56;
                                if (offset + 56 <= _testCmdResponse.Length)
                                {
                                    byte[] telemetryPage = new byte[60];
                                    telemetryPage[0] = 0x81;
                                    telemetryPage[1] = _testCmdTelemetryDeviceId;
                                    telemetryPage[2] = _testCmdTelemetryActionId;
                                    telemetryPage[3] = _testCmdPagesRemaining > 1 ? (byte)0x03 : (byte)0x02;
                                    _testCmdResponse.AsSpan(offset, 56).CopyTo(telemetryPage.AsSpan(4));
                                    response = telemetryPage;
                                    responseLen = 60;
                                    _testCmdPagesRemaining--;
                                    if (_testCmdPagesRemaining == 0)
                                    {
                                        _testCmdReady = false;
                                    }
                                    int telemN = Math.Min(responseLen, wLength);
                                    SendRetSubmit(cmd.Seqnum, 0, telemN, response.AsSpan(0, telemN).ToArray());
                                    return;
                                }
                            }

                            // Normal multi-page or single-page response
                            if (_testCmdPagesRemaining == 0)
                            {
                                // Last page
                                response = _testCmdResponse;
                                responseLen = _testCmdResponseLength;
                                _testCmdReady = false;
                            }
                            else
                            {
                                // Intermediate page
                                response = _testCmdResponse;
                                responseLen = 56;
                                _testCmdPagesRemaining--;
                            }
                        }
                        else
                        {
                            // Single-page response 鈥?check if it's a "standard" handler response
                            // that needs [0x81, deviceId, actionId, status, data...] header injected.
                            // Standard handlers (HandleBtMac, HandleBatteryVoltage, etc.) return
                            // raw data without any header. HandleTestCommand already prepends 0x81.
                            // We distinguish them by checking if _testCmdStandardDeviceId is set:
                            // HandleTestCommand sets device_id=1 and stores actionResponses that
                            // already start with [0x81, 0x01, ...]. Other handlers don't set the
                            // _testCmdStandardDeviceId field in their SET_REPORT path.
                            if (_testCmdStandardDeviceId != 0 && _testCmdResponseLength > 0)
                            {
                                // Standard handler response 鈥?needs full header injection
                                int dataLen = _testCmdResponseLength;
                                int maxResponseLen = Math.Min(4 + dataLen, 256);
                                if (maxResponseLen > wLength)
                                {
                                    maxResponseLen = wLength;
                                }
                                byte[] headeredResponse = new byte[Math.Min(4 + dataLen, ResponseBufferMaxLength)];
                                headeredResponse[0] = 0x81;
                                headeredResponse[1] = _testCmdStandardDeviceId;
                                headeredResponse[2] = _testCmdStandardActionId;
                                headeredResponse[3] = 0x02; // COMPLETE
                                _testCmdResponse.AsSpan(0, dataLen).CopyTo(headeredResponse.AsSpan(4));
                                response = headeredResponse;
                                responseLen = Math.Min(4 + dataLen, wLength);
                                _testCmdStandardDeviceId = 0;
                                _testCmdStandardActionId = 0;
                                _testCmdReady = false;
                            }
                            else
                            {
                                // HandleTestCommand response 鈥?already has [0x81, deviceId, actionId, status, data...]
                                response = _testCmdResponse;
                                responseLen = _testCmdResponseLength;
                                _testCmdReady = false;
                            }
                        }

                        int n = Math.Min(responseLen, wLength);
                        SendRetSubmit(cmd.Seqnum, 0, n, response.AsSpan(0, n).ToArray());
                        return;
                    }

                    var stub = BuildFeatureStub(reportId, wLength);
                    if (stub == null) { SendError(cmd.Seqnum, -UsbipProtocol.EPipe); return; }
                    PublishOutput(SourceHidFeatureRead, reportId, ReadOnlySpan<byte>.Empty);
                    int stubLen = Math.Min(stub.Length, wLength);
                    SendRetSubmit(cmd.Seqnum, 0, stubLen, stub.AsSpan(0, stubLen).ToArray());
                    return;
                }
                break;
            }
            case 0x09 when !deviceToHost: // SET_REPORT
            {
                var payload = outPayload ?? Array.Empty<byte>();
                // Resolution Multiplier feature (report 0, no report-ID byte in
                // the payload): store the host's value and ack.
                if (_resolutionMultiplier > 0 && reportType == 0x03 && reportId == 0)
                {
                    if (payload.Length >= 1) _resolutionMultiplierValue = payload[0];
                    SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
                    return;
                }
                byte rid = payload.Length > 0 ? payload[0] : reportId;
                var data = payload.Length > 0 ? payload.AsSpan(1) : ReadOnlySpan<byte>.Empty;

                // Switch Pro: keyboard-style SetReport routing of the 0x80/0x01/
                // 0x10 output reports (some HID stacks send these via the control
                // pipe when no interrupt OUT transfer is pending).
                if (_switchProtocol && reportType != 0x03
                    && (rid == 0x80 || rid == 0x01 || rid == 0x10))
                {
                    HandleSwitchOutput(rid, data);
                    SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
                    return;
                }

                // DS4 authentication protocol:
                // SET_FEATURE 0x80 with [0x03, 0x03] -> next GET_FEATURE 0x81 returns challenge response
                // SET_FEATURE 0x80 with [0x03, 0x01] -> next GET_FEATURE 0x20 returns serial (already in Ds4FeatureReport20)
                if (reportType == 0x03 && rid == 0x80 && data.Length >= 2 && data[0] == 0x03)
                {
                    // DS4 authentication: [0x03, 0x03] = challenge, [0x03, 0x01] = serial
                    // state tracking is unused; the reports are already served correctly
                }

                // Sony DualSense test command: SET_FEATURE 0x80 with [device_id, action_id, ...]
                if (reportType == 0x03 && rid == 0x80 && data.Length >= 2)
                {
                    byte deviceId = data[0];
                    byte actionId = data[1];

                    if (deviceId == 0x01 && actionId < _testCmd._actionResponses.Length)
                    {
                        // Standard test command (device_id=1)
                        var respSpan = _testCmdResponse.AsSpan(0);
                        int len = _testCmd.HandleTestCommand(data, respSpan);
                        if (len > 0)
                        {
                            _testCmdResponseLength = len;
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 0; // single-page response
                        }
                    }
                    else if (deviceId == 0x01 && actionId == 0x70)
                    {
                        // Telemetry query (device_id=0x01, action_id=0x70)
                        // Store telemetry params for GET_REPORT header injection
                        _testCmdTelemetryDeviceId = 0x70; // DEVICE_TELEMETRY
                        _testCmdTelemetryActionId = 0x01; // GET_INFO
                        // Write 4 pages 脳 56 bytes into response buffer
                        int pages = _testCmd.HandleTelemetry(_testCmdResponse, 4);
                        if (pages < 0)
                        {
                            // Multi-page response: 4 pages, _testCmdResponseLength < 0 marks telemetry page count
                            _testCmdResponseLength = -(4); // negative = -totalPages
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 4; // 4 remaining pages including current
                        }
                        else if (pages > 0)
                        {
                            _testCmdResponseLength = pages;
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 0;
                        }
                    }
                    else if (deviceId == 0x07 && actionId == 0x25)
                    {
                        // Action 37: Type 2 tracability (adaptive trigger info)
                        _testCmdStandardDeviceId = 0x07;
                        _testCmdStandardActionId = 0x25;
                        // data[2] = 1 for left, 2 for right
                        byte param = data.Length > 2 ? data[2] : (byte)0;
                        var respSpan = _testCmdResponse.AsSpan(0);
                        int len = _testCmd.HandleType2Tracability(param, respSpan);
                        if (len > 0)
                        {
                            _testCmdResponseLength = len;
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 0;
                        }
                    }
                    else if (deviceId == 0x09 && actionId == 0x02)
                    {
                        // BT MAC address
                        _testCmdStandardDeviceId = 0x09;
                        _testCmdStandardActionId = 0x02;
                        var respSpan = _testCmdResponse.AsSpan(0);
                        int len = _testCmd.HandleBtMac(respSpan);
                        if (len > 0)
                        {
                            _testCmdResponseLength = len;
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 0;
                        }
                    }
                    else if (deviceId == 0x04 && actionId == 0x03)
                    {
                        // Battery voltage
                        _testCmdStandardDeviceId = 0x04;
                        _testCmdStandardActionId = 0x03;
                        var respSpan = _testCmdResponse.AsSpan(0);
                        int len = _testCmd.HandleBatteryVoltage(respSpan);
                        if (len > 0)
                        {
                            _testCmdResponseLength = len;
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 0;
                        }
                    }
                    else if (deviceId == 0x09 && actionId == 0x0A)
                    {
                        // BT patch info
                        _testCmdStandardDeviceId = 0x09;
                        _testCmdStandardActionId = 0x0A;
                        var respSpan = _testCmdResponse.AsSpan(0);
                        int len = _testCmd.HandleBtPatchInfo(respSpan);
                        if (len > 0)
                        {
                            _testCmdResponseLength = len;
                            _testCmdReady = true;
                            _testCmdPagesRemaining = 0;
                        }
                    }
                }

                PublishOutput(reportType == 0x03 ? SourceHidFeature : SourceHidOutput, rid, data);
                SendRetSubmit(cmd.Seqnum, 0, payload.Length, null);
                return;
            }
            case 0x0A when !deviceToHost: // SET_IDLE
                SendRetSubmit(cmd.Seqnum, 0, 0, null);
                return;
            case 0x02 when deviceToHost: // GET_IDLE
                SendRetSubmit(cmd.Seqnum, 0, Math.Min(1, (int)wLength),
                    wLength > 0 ? new byte[] { 0 } : null);
                return;
        }
        // GET/SET_PROTOCOL and anything else: the pad is not a boot device;
        // a real one stalls these.
        SendError(cmd.Seqnum, -UsbipProtocol.EPipe);
    }

    /// <summary>driver.c's Sony Get_Feature stub table, sized against
    /// wLength the way the driver sizes its feature reads.</summary>
    private byte[]? BuildFeatureStub(byte reportId, ushort wLength)
    {
        if (Descriptors.VendorId != 0x054C) return null;
        switch (reportId)
        {
            case 0x05:
                if (wLength < 41) return null;
                { var p = new byte[41]; p[0] = reportId; SonyCalibration.CopyTo(p, 1); return p; }
            case 0x09:
                if (wLength < 20) return null;
                {
                    var p = new byte[20];
                    p[0] = reportId;
                    p[1] = 0x02; p[2] = 0x48; p[3] = 0x4D; // locally administered, 'H' 'M'
                    p[4] = 0x00; p[5] = 0x00; p[6] = (byte)_index;
                    return p;
                }
            case 0x20:
                // DS4 feature report 0x20: 64-byte structure with serial at offset 12-20.
                // DualSense uses Ds5FirmwareInfo; DS4 uses Ds4FeatureReport20.
                if (wLength < 64) return null;
                {
                    if (Descriptors.ProductId == 0x09CC || Descriptors.ProductId == 0x05C4)
                    {
                        // DS4 (CUH-ZCT1/2)
                        return (byte[])Ds4FeatureReport20.Clone();
                    }
                    var p = (byte[])Ds5FirmwareInfo.Clone();
                    if (Descriptors.ProductId == 0x0DF2)
                    {
                        p[22] = 0x44; p[23] = 0x00;
                        p[44] = 0x17; p[45] = 0x02;
                    }
                    return p;
                }
            case 0x81:
                // DS4 authentication challenge response (after SET_FEATURE 0x80 subcommand 0x03).
                // Returns 50528769 (0x03030301) big-endian at offset 1.
                if (wLength < 64) return null;
                return (byte[])Ds4FeatureReport81.Clone();
            case 0x12:
                // DS4 pairing info over USB. The DS4's equivalent of 0x09,
                // and the one Sony read whose absence is fatal rather than
                // cosmetic: hid-playstation's caller aborts device creation
                // when it fails. MAC at bytes 1..6, non-zero, locally
                // administered, matching the 0x09 path exactly.
                if (wLength < 16) return null;
                {
                    var p = new byte[16];
                    p[0] = reportId;
                    p[1] = 0x02; p[2] = 0x48; p[3] = 0x4D; // locally administered, 'H' 'M'
                    p[4] = 0x00; p[5] = 0x00; p[6] = (byte)_index;
                    return p;
                }
            case 0x22:
                // Bluetooth patch info. Zero past the report ID is the real
                // answer for a pad carrying no patch, and dualsense-tester
                // skips the row on a falsy value. It only has to exist
                // because the real 0x20 above opens the traceability branch
                // that reads it; see driver.c for the full reasoning.
                if (wLength < 64) return null;
                { var p = new byte[64]; p[0] = reportId; return p; }
            case 0x02:
                if (wLength >= 41) { var p = new byte[41]; p[0] = reportId; SonyCalibration.CopyTo(p, 1); return p; }
                if (wLength >= 37) { var p = new byte[37]; p[0] = reportId; SonyCalibration.CopyTo(p, 1); return p; }
                return null;
            case 0xA3:
                if (wLength < 49) return null;
                { var p = (byte[])Ds4FirmwareInfo.Clone(); return p; }
            default:
                return null;
        }
    }

    /// <summary>Neutral Sony motion calibration, byte-for-byte the same
    /// payload driver.c serves (g_SonyCalibration, issue #43). Written at
    /// offset 1, after the report id. A consumer reading calibration from
    /// a composite persona has to see exactly what the real pad reports,
    /// so these bytes must not diverge from driver.c's.</summary>
    private static readonly byte[] SonyCalibration =
    {
        0x00, 0x00,  // gyro_pitch_bias
        0x00, 0x00,  // gyro_yaw_bias
        0x00, 0x00,  // gyro_roll_bias
        0x10, 0x27,  // gyro_pitch_plus   +10000
        0xF0, 0xD8,  // gyro_pitch_minus  -10000
        0x10, 0x27,  // gyro_yaw_plus     +10000
        0xF0, 0xD8,  // gyro_yaw_minus    -10000
        0x10, 0x27,  // gyro_roll_plus    +10000
        0xF0, 0xD8,  // gyro_roll_minus   -10000
        0xF4, 0x01,  // gyro_speed_plus     +500
        0xF4, 0x01,  // gyro_speed_minus    +500
        0x10, 0x27,  // acc_x_plus        +10000
        0xF0, 0xD8,  // acc_x_minus       -10000
        0x10, 0x27,  // acc_y_plus        +10000
        0xF0, 0xD8,  // acc_y_minus       -10000
        0x10, 0x27,  // acc_z_plus        +10000
        0xF0, 0xD8,  // acc_z_minus       -10000
    };

    /// <summary>DS5 firmware info, byte-for-byte the same payload driver.c
    /// serves (ds5FirmwareInfo, issue #43). Captured from a real wired
    /// DualSense during an F1 22 startup trace. ASCII build date
    /// "Jul  4 2025" and time "10:10:32", then fwType 3, hwInfo 0x1310 and
    /// the firmware versions.
    ///
    /// <para>Served verbatim. F1 22 validates this blob and abandons the
    /// device on the zeros it used to get, and which field it validates is
    /// not known, so no byte here is synthesised. See driver.c for the
    /// offset agreement between hid-playstation.c and dualsense-tester, and
    /// for why WinUHid's own default is not used.</para></summary>
    private static readonly byte[] Ds5FirmwareInfo =
    {
        0x20, 0x4A, 0x75, 0x6C, 0x20, 0x20, 0x34, 0x20,
        0x32, 0x30, 0x32, 0x35, 0x31, 0x30, 0x3A, 0x33,
        0x38, 0x00, 0x00, 0x00, 0x02, 0x00, 0x0B, 0x00,
        0x07, 0x11, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x01,
        0x01, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x30, 0x06, 0x00, 0x00,
        0x3C, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x02, 0x00,
        0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    /// <summary>Get DS5 firmware info for external access (testing, etc.).</summary>
    public static byte[] GetDs5FirmwareInfo() => (byte[])Ds5FirmwareInfo.Clone();

    /// <summary>DS4 firmware / hardware info, verbatim from WinUHid's
    /// WinUHidPS4.cpp. ASCII build date "Aug  3 2013" and time "07:01:12"
    /// followed by the hardware and firmware words. Byte 28 (0-indexed) high
    /// byte = 0xA4 (164) encodes motherboard model JDM-050 per the test-tool
    /// mapping: e >> 8 == 164 鈫?"JDM-050".</summary>
    private static readonly byte[] Ds4FirmwareInfo =
    {
        0xA3, 0x41, 0x75, 0x67, 0x20, 0x20, 0x33, 0x20,
        0x32, 0x30, 0x31, 0x33, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x30, 0x37, 0x3A, 0x30, 0x31, 0x3A, 0x31,
        0x32, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x01, 0x00, 0xA4, 0x03, 0x00, 0x00,
        0x00, 0x49, 0x00, 0x05, 0x00, 0x00, 0x80, 0x03,
        0x00
    };

    /// <summary>DS4 feature report 0x20 (32) 鈥?64-byte structure used by
    /// hid-playstation's authentication (checkReportStructure, getSerialNumber).
    /// Byte 0 = report ID (0x20). Bytes 12-27 = UTF-16LE serial number "JDM-050".</summary>
    private static readonly byte[] Ds4FeatureReport20 =
    {
        0x20, // report ID
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 1-8
        0x00, 0x00, 0x00, 0x00,                            // 9-12
        // Serial "JDM-050" as UTF-16LE at offset 12 (13th byte):
        0x4A, 0x00, // 'J'
        0x44, 0x00, // 'D'
        0x4D, 0x00, // 'M'
        0x2D, 0x00, // '-'
        0x30, 0x00, // '0'
        0x35, 0x00, // '5'
        0x30, 0x00, // '0'
        0x00, 0x00, // null terminator
        // rest zeroed
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>DS4 feature report 0x81 (129) 鈥?authentication challenge response.
    /// Returned after host sends SET_FEATURE 0x80 with subcommand 0x03.
    /// Value 50528769 (0x03030301) or 50528768 (0x03030300) at offset 1 (big-endian).</summary>
    private static readonly byte[] Ds4FeatureReport81 =
    {
        0x81, // report ID
        0x03, 0x03, 0x03, 0x01, // 50528769 big-endian at offset 1
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };

    private void ResetDeviceState()
    {
        _configurationValue = 0;
        ResetAltSettings();
        Audio?.Clear();
        lock (_hidLock)
        {
            _pendingInterruptIn.Clear();
            _frameQueue.Clear();
            _switchReplies.Clear();
            _gipReplies.Clear();
        }
        _switchImuEnabled = false;
        Interlocked.Exchange(ref _switchTimerCounter, 0);
        _lastSwitchSharedSeqNo = 0;
        _gip?.Reset();
        _lastGipSharedSeqNo = 0;
    }

    private void ResetAltSettings()
    {
        if (Audio == null) return;
        foreach (var kv in Descriptors.Endpoints)
        {
            if (kv.Value.TransferType == 1)
                Audio.SetAltSetting(kv.Value.InterfaceNumber, 0);
        }
    }

    // 鈹€鈹€ Reply plumbing 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
    //
    // Sends are guarded: the pacing pump and the input pump complete URBs
    // from their own threads, and a socket being torn down mid-completion
    // must not take a background thread (and with it the process) down.
    // The connection's own reader thread notices the closed socket and
    // runs the detach path.

    private void SendInterruptInReply(uint seqnum, byte[] frame)
        => SendRetSubmit(seqnum, 0, frame.Length, frame);

    private void SendError(uint seqnum, int status)
        => SendRetSubmit(seqnum, status, 0, null);

    private void SendRetSubmit(uint seqnum, int status, int actualLength, byte[]? inPayload)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitNonIso(seqnum, status, actualLength, inPayload); }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _gipSendErrCount) <= 5)
                GipLog.Write($"SEND FAIL seq={seqnum} status={status} len={actualLength}: {ex.Message}");
        }
    }

    /// <summary>Completion callback from the audio engine's pacing thread.
    /// Builds the isochronous RET_SUBMIT per the 0.9.8.0 receive rules.</summary>
    private void CompleteIsoOnWire(UsbAudioEngine.PendingIso p, byte[]? inCompacted, int perPacketActual)
    {
        var c = _connection;
        if (c == null) return;
        try { c.SendRetSubmitIso(p, inCompacted, perPacketActual); } catch { }
    }

    public void Dispose()
    {
        _stop = true;
        _connection = null;
        Audio?.Dispose();
        try { _inputThread.Join(600); } catch { }
        if (_inputWaitEvent != IntPtr.Zero)
        {
            CloseHandle(_inputWaitEvent);
            _inputWaitEvent = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}



