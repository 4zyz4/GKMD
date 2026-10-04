using System;
using System.Collections.Generic;

namespace GKMD.Internal.Usbip;

/// <summary>Device side of Microsoft's Gaming Input Protocol (GIP) for the
/// wired Xbox One persona, implemented against [MS-GIPUSB] (the USB
/// extension), not xone's Linux dialect.
///
/// <para>Handshake: the device sends a <b>Hello Device</b> (0x02) every
/// 500 ms until the host answers with a <b>Metadata Request</b> (0x04) or a
/// <b>Set Device State: START</b> (0x05/0x00). The device answers the
/// metadata request with the compiled metadata blob, fragmented as
/// initial/middle/final plus a complete message. Once the host sets START
/// the device streams <b>Gamepad Input Reports</b> (0x20) and answers
/// rumble (0x09), LED (0x0A) and serial (0x1E) requests.</para>
///
/// <para>The compiled metadata blob below is taken verbatim from
/// [MS-GIPUSB] section 2.2.2 ("GIP Gamepad metadata Example"): a gamepad
/// with preferred type "Windows.Xbox.Input.Gamepad", the IController /
/// IGamepad / INavigationController interface GUIDs, and the 0x20 (input)
/// and 0x09 (rumble) message declarations. It carries no HID report
/// descriptor; Windows composes the HID child itself.</para>
///
/// <para>This class produces frames only; <see cref="UsbipEmulatedDevice"/>
/// owns the interrupt-IN queue and the output-ring publish.</para></summary>
internal sealed class GipResponder
{
    // Attachment / client id; a single wired pad is attachment 0.
    private const byte ClientId = 0;

    private const ushort VendorId = 0x045E;
    // Xbox One (Model 1708) product id; the Xbox Series X|S (Model 1914) wired
    // pad reports 0x0B12 instead.
    private const ushort ProductId = 0x02EA;
    private const ushort SeriesProductId = 0x0B12;

    private readonly Action<byte, byte, byte[]> _publishOutput;

    // True for the Xbox Series X|S persona: the metadata advertises the
    // console-function-map and dynamic-latency interfaces and the input report
    // is 40 bytes (14-byte gamepad payload + 18-byte map + 8-byte latency).
    private readonly bool _series;
    private readonly ushort _productId;

    private readonly byte[] _hello;
    private readonly byte[] _status;
    private readonly byte[] _metadata;
    private readonly byte[] _serial;

    private byte _outSeq;
    private byte _inputSeq;
    private bool _hostSeen;
    private bool _active;

    private readonly byte[] _payload = new byte[14];
    private bool _havePayload;
    private bool _guideInitialized;
    private bool _lastGuide;
    // Series Share button, carried in the console function map byte (map[0]
    // bit 0) appended to the input report.
    private bool _share;

    public bool HostSeen => _hostSeen;
    public bool Active => _active;
    public bool Guide => (_payload[0] & 0x01) != 0;

    public GipResponder(Action<byte, byte, byte[]> publishOutput, int index, bool series = false)
    {
        _publishOutput = publishOutput;
        _series = series;
        _productId = series ? SeriesProductId : ProductId;
        _hello = BuildHello(index);
        _status = BuildStatus();
        _metadata = series ? BuildSeriesMetadata() : Convert.FromHexString(MetadataBlobHex);
        _serial = BuildSerial(index);
        if (!series && _metadata.Length != 182)
            throw new InvalidOperationException($"GIP metadata blob is {_metadata.Length} bytes, expected 182.");
    }

    private byte NextSeq()
    {
        byte s = ++_outSeq;
        if (s == 0) s = ++_outSeq; // sequence is never zero
        return s;
    }

    /// <summary>The Low Latency (input) data class has its own wrapping
    /// sequence counter, incremented by one per input report (spec 2.2.10:
    /// "Wrapping counter for Low Latency Data Class").</summary>
    private byte NextInputSeq()
    {
        byte s = ++_inputSeq;
        if (s == 0) s = ++_inputSeq;
        return s;
    }

    /// <summary>Hello Device (0x02), [MS-GIPUSB] Table 27.</summary>
    public byte[] EncodeHello()
        => GipProtocol.EncodeFrame(GipProtocol.CmdAnnounce,
            (byte)(GipProtocol.OptInternal | ClientId), NextSeq(), _hello);

    /// <summary>Extended Status Device (0x03), 4-byte payload with no events.
    /// [MS-GIPUSB] Table 29.</summary>
    public byte[] EncodeStatus()
        => GipProtocol.EncodeFrame(GipProtocol.CmdStatus,
            (byte)(GipProtocol.OptInternal | ClientId), NextSeq(), _status);

    /// <summary>Metadata Response (0x04): the compiled blob, fragmented.
    /// [MS-GIPUSB] Tables 35-38.</summary>
    public List<byte[]> EncodeMetadata()
        => GipProtocol.EncodeMessage(GipProtocol.CmdIdentify,
            (byte)(GipProtocol.OptInternal | ClientId), NextSeq(), _metadata);

    /// <summary>Guide Button Status (0x07), upstream, [MS-GIPUSB] section
    /// 3.1.5.5.6.</summary>
    public byte[] EncodeGuide(bool down)
    {
        Span<byte> p = stackalloc byte[2];
        p[0] = down ? (byte)1 : (byte)0;
        p[1] = 0x5B; // VK_LWIN; the host treats the key byte as opaque
        return GipProtocol.EncodeFrame(GipProtocol.CmdVirtualKey,
            (byte)(GipProtocol.OptAck | GipProtocol.OptInternal | ClientId), NextSeq(), p);
    }

    /// <summary>Copies the latest input payload. Returns true when anything
    /// (the 14-byte gamepad body, or the Series Share flag) actually changed.
    /// On the Series persona the shared payload is 15 bytes: the 14-byte body
    /// plus a trailing console-function-map byte whose bit 0 is Share.</summary>
    public bool UpdatePayload(ReadOnlySpan<byte> body)
    {
        if (body.Length < XboxOnePayloadSize) return false;
        bool changed = false;
        if (!_havePayload || !body.Slice(0, XboxOnePayloadSize).SequenceEqual(_payload))
        {
            body.Slice(0, XboxOnePayloadSize).CopyTo(_payload);
            _havePayload = true;
            changed = true;
        }
        if (_series && body.Length > XboxOnePayloadSize)
        {
            bool share = (body[XboxOnePayloadSize] & 0x01) != 0;
            if (share != _share)
            {
                _share = share;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Reports and consumes a Guide state transition.</summary>
    public bool TakeGuideChange(out bool down)
    {
        down = Guide;
        if (!_guideInitialized)
        {
            _guideInitialized = true;
            _lastGuide = down;
            return down;
        }
        if (down == _lastGuide) return false;
        _lastGuide = down;
        return true;
    }

    /// <summary>Gamepad Input Report (0x20), [MS-GIPUSB] Table 57. The Xbox One
    /// persona sends the 14-byte gamepad body; the Series persona pads it to 40
    /// bytes with the 18-byte console function map (map[0] bit 0 = Share) and an
    /// 8-byte dynamic-latency block, matching the layout Windows and SDL expect
    /// for a Series pad.</summary>
    public byte[] BuildInputFrame()
    {
        int size = _series ? SeriesInputSize : XboxOnePayloadSize;
        Span<byte> p = stackalloc byte[SeriesInputSize];
        _payload.CopyTo(p);
        p[0] &= 0xFE; // strip the internal Guide marker (bit 0 is reserved)
        if (_series)
        {
            // Console function map (offset 14, 18 bytes) then dynamic latency
            // (offset 32, 8 bytes). Only map[0] bit 0 (Share) is meaningful to
            // the host; the rest stays zero.
            p[XboxOnePayloadSize] = _share ? (byte)0x01 : (byte)0x00;
        }
        return GipProtocol.EncodeFrame(GipProtocol.CmdInput, 0x00, NextInputSeq(), p.Slice(0, size));
    }

    public void Reset()
    {
        _hostSeen = false;
        _active = false;
        _inputSeq = 0;
        _havePayload = false;
        _guideInitialized = false;
        _lastGuide = false;
        _share = false;
        Array.Clear(_payload);
    }

    private const int XboxOnePayloadSize = 14;
    // 14-byte gamepad body + 18-byte console function map + 8-byte dynamic latency.
    private const int SeriesInputSize = 40;

    // ── Host → device ────────────────────────────────────────────────────

    public void HandleHostBuffer(ReadOnlySpan<byte> data, Action<byte[]> emitReply)
    {
        int pos = 0;
        while (data.Length - pos > 3)
        {
            int off = pos;
            if (!GipProtocol.TryDecodeHeader(data, ref off, out byte cmd, out byte opt,
                    out byte seq, out int plen, out int chunkOff))
                break;
            if (plen < 0 || off + plen > data.Length)
            {
                GipLog.Write($"  bad frame cmd=0x{cmd:X2} opt=0x{opt:X2} len={plen} remain={data.Length - pos}");
                break;
            }

            ProcessFrame(cmd, opt, seq, chunkOff, data.Slice(off, plen), emitReply);
            pos = off + plen;
        }
    }

    private void ProcessFrame(byte cmd, byte opt, byte seq, int chunkOff,
                              ReadOnlySpan<byte> payload, Action<byte[]> emitReply)
    {
        _hostSeen = true;

        // Reliable messages ask for an acknowledgement; answer before any
        // side effects so the host can advance its fragment state machine.
        if ((opt & GipProtocol.OptAck) != 0)
            emitReply(BuildAck(seq, cmd, opt));

        switch (cmd)
        {
            case GipProtocol.CmdIdentify: // Metadata Request (0x04)
                GipLog.Write($"  -> METADATA response ({_metadata.Length} bytes, fragmented)");
                foreach (var frame in EncodeMetadata()) emitReply(frame);
                break;

            case GipProtocol.CmdPower: // Set Device State (0x05)
            {
                // 0x00 = START. 0x04 OFF / 0x07 RESET stop the input stream.
                // Other values (including the extended 0x06 "US" init packet)
                // leave the current state unchanged.
                byte state = payload.Length > 0 ? payload[0] : (byte)0;
                if (state == 0x01 || state == 0x04 || state == 0x07) _active = false;
                else _active = true; // START (0x00) and the 0x06 "US" init both run the pad
                GipLog.Write($"  -> SET_DEVICE_STATE=0x{state:X2} len={payload.Length} active={_active}, STATUS reply");
                emitReply(EncodeStatus());
                break;
            }

            case GipProtocol.CmdRumble: // 0x09, downstream
                _publishOutput(UsbipEmulatedDevice.SourceHidOutput, GipProtocol.CmdRumble, payload.ToArray());
                break;

            case GipProtocol.CmdSerialNumber: // 0x1E Get Serial Number
                GipLog.Write("  -> SERIAL reply");
                emitReply(GipProtocol.EncodeFrame(GipProtocol.CmdSerialNumber,
                    (byte)(GipProtocol.OptInternal | ClientId), NextSeq(), _serial));
                break;

            // LED (0x0A), Authenticate (0x06), Acknowledge (0x01) and any
            // unknown command: accepted, no reply.
            default:
                break;
        }
    }

    private static byte[] BuildAck(byte seq, byte command, byte options)
    {
        Span<byte> p = stackalloc byte[9];
        p[0] = 0x00; // control code: ACK
        p[1] = command;
        p[2] = (byte)(options & GipProtocol.OptInternal);
        GipProtocol.WriteU16LE(p, 3, 0); // fragment offset
        GipProtocol.WriteU16LE(p, 5, 0); // padding
        GipProtocol.WriteU16LE(p, 7, 0); // bytes remaining
        return GipProtocol.EncodeFrame(GipProtocol.CmdAcknowledge,
            (byte)(GipProtocol.OptInternal | ClientId), seq, p);
    }

    // ── Capability blobs ─────────────────────────────────────────────────

    /// <summary>Hello Device payload (28 bytes), [MS-GIPUSB] Table 27:
    /// 8-byte DeviceID (bytes 6-7 zero), VendorID, ProductID, four firmware
    /// words, hardware major/minor, then the mandatory RF / security / GIP
    /// protocol versions (all 1.0).</summary>
    private byte[] BuildHello(int index)
    {
        var p = new byte[28];
        p[0] = 0x02; p[1] = 0x48; p[2] = 0x4D; p[3] = 0x00; p[4] = 0x00; p[5] = (byte)index;
        // p[6..7] = 0 (DeviceID high bytes MUST be zero)
        GipProtocol.WriteU16LE(p, 8, VendorId);
        GipProtocol.WriteU16LE(p, 10, _productId);
        // Firmware 1.0.0.0 (major/minor must match metadata's list).
        GipProtocol.WriteU16LE(p, 12, 1); GipProtocol.WriteU16LE(p, 14, 0);
        GipProtocol.WriteU16LE(p, 16, 0); GipProtocol.WriteU16LE(p, 18, 0);
        // Hardware major/minor.
        p[20] = 1; p[21] = 0;
        // RF protocol 1.0, security protocol 1.0, GIP 1.0.
        p[22] = 1; p[23] = 0;
        p[24] = 1; p[25] = 0;
        p[26] = 1; p[27] = 0;
        return p;
    }

    /// <summary>Extended Status payload (4 bytes): status + extended status +
    /// two reserved. Bit 7 online, bit 4 charging, bits 2-3 power source,
    /// bits 0-1 capacity.</summary>
    private static byte[] BuildStatus()
    {
        var p = new byte[4];
        p[0] = 0x87; // online | alkaline | full
        return p;
    }

    /// <summary>Get Serial Number response payload (0x1E): command 0x04,
    /// status 0 (OK), then N serial bytes (12..32). [MS-GIPUSB] Table 51.</summary>
    private static byte[] BuildSerial(int index)
    {
        var serial = System.Text.Encoding.ASCII.GetBytes($"XBOX1{index:D9}"); // 14 chars
        var p = new byte[2 + serial.Length];
        p[0] = 0x04; // command: Get Serial Number
        p[1] = 0x00; // status: OK
        serial.CopyTo(p, 2);
        return p;
    }

    // Xbox Series X|S metadata: the same shape as the [MS-GIPUSB] 2.2.2
    // example, but the supported-interface list adds the console-function-map
    // and dynamic-latency GUIDs and the 0x20 input message is declared 40 bytes
    // (14-byte gamepad body + 18-byte console function map + 8-byte dynamic
    // latency). Built by splicing the proven 182-byte Xbox One blob, so every
    // offset, the firmware block and the message/preferred-type blocks stay
    // byte-for-byte identical to the validated persona.
    private static byte[] BuildSeriesMetadata()
    {
        var baseBlob = Convert.FromHexString(MetadataBlobHex); // 182 bytes
        const int deviceStart = 16;
        const int supportedOffset = 86;               // count byte of the Xbox One 3-GUID list
        const int baseDeviceLength = 119;
        const int messagesStart = deviceStart + baseDeviceLength; // 135

        var guids = new[]
        {
            // IController {9776ff56-9bfd-4581-ad45-b645bba526d6}
            new byte[] { 0x56, 0xFF, 0x76, 0x97, 0xFD, 0x9B, 0x81, 0x45, 0xAD, 0x45, 0xB6, 0x45, 0xBB, 0xA5, 0x26, 0xD6 },
            // IGamepad {082e402c-07df-45e1-a5ab-a3127af197b5}
            new byte[] { 0x2C, 0x40, 0x2E, 0x08, 0xDF, 0x07, 0xE1, 0x45, 0xA5, 0xAB, 0xA3, 0x12, 0x7A, 0xF1, 0x97, 0xB5 },
            // IDevAuthPCOptOut {7a34ce77-7de2-45c6-8ca4-0042c08bd94a}
            new byte[] { 0x77, 0xCE, 0x34, 0x7A, 0xE2, 0x7D, 0xC6, 0x45, 0x8C, 0xA4, 0x00, 0x42, 0xC0, 0x8B, 0xD9, 0x4A },
            // IConsoleFunctionMap_InputReport {ecddd2fe-d387-4294-bd96-1a712e3dc77d}
            new byte[] { 0xFE, 0xD2, 0xDD, 0xEC, 0x87, 0xD3, 0x94, 0x42, 0xBD, 0x96, 0x1A, 0x71, 0x2E, 0x3D, 0xC7, 0x7D },
            // DynamicLatencyInput {87f2e56b-c3bb-49b1-8265-fffff37799ee}
            new byte[] { 0x6B, 0xE5, 0xF2, 0x87, 0xBB, 0xC3, 0xB1, 0x49, 0x82, 0x65, 0xFF, 0xFF, 0xF3, 0x77, 0x99, 0xEE },
        };

        int supportedLen = 1 + guids.Length * 16;                        // 81
        int deviceLength = (supportedOffset - deviceStart) + supportedLen; // 151
        int messagesLen = baseBlob.Length - messagesStart;               // 47
        int total = deviceStart + deviceLength + messagesLen;            // 214

        var blob = new byte[total];
        Array.Copy(baseBlob, 0, blob, 0, supportedOffset);
        blob[deviceStart] = (byte)(deviceLength & 0xFF);
        blob[deviceStart + 1] = (byte)(deviceLength >> 8);

        int p = supportedOffset;
        blob[p++] = (byte)guids.Length;
        foreach (var g in guids)
        {
            Array.Copy(g, 0, blob, p, 16);
            p += 16;
        }

        Array.Copy(baseBlob, messagesStart, blob, p, messagesLen);
        // First message metadata entry follows the num_messages byte: patch its
        // 0x20 input payload length from 14 to 40.
        int firstMessage = p + 1;
        blob[firstMessage + 3] = SeriesInputSize;
        blob[firstMessage + 4] = 0;

        blob[14] = (byte)(total & 0xFF);
        blob[15] = (byte)(total >> 8);
        return blob;
    }

    // Compiled GIP Gamepad metadata blob, 182 bytes, from [MS-GIPUSB]
    // section 2.2.2, with one deliberate edit: the third SupportedInterfaces
    // GUID (INavigationController) is replaced, byte-for-byte in place, with
    // the security-exchange opt-out GUID 7a34ce77-7de2-45c6-8ca4-0042c08bd94a
    // (bytes 0x77..0x86). [MS-GIPUSB] 5.1: "Controllers that talk to a
    // Windows PC SHOULD use the following GUID to opt-out of the security
    // exchange over USB... The host succeeds the security exchange by
    // default." Replacing a GUID keeps the element count and every offset
    // unchanged, so the blob stays 182 bytes. IController and IGamepad are
    // preserved; INavigationController is not needed by a plain gamepad.
    //
    // Preferred type "Windows.Xbox.Input.Gamepad", interfaces
    // IController/IGamepad/[opt-out], and the 0x20 (input, 14 bytes,
    // upstream) and 0x09 (rumble, 9 bytes, downstream) message
    // declarations.
    private const string MetadataBlobHex =
        "1000010000000000000000000000B600" +
        "770016001B001C002300290046000000" +
        "00000000000001010000000006010203" +
        "04060705010405060A011A0057696E64" +
        "6F77732E58626F782E496E7075742E47" +
        "616D657061640356FF7697FD9B8145AD" +
        "45B645BBA526D62C402E08DF07E145A5" +
        "ABA3127AF197B577CE347AE27DC6458C" +
        "A40042C08BD94A021700200E00010010" +
        "00000000000000000000000000000017" +
        "00090900010008000000000000000000" +
        "000000000000";
}
