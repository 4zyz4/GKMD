using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GKMD.Internal;

/// <summary>Issue #39. A USB configuration a composite persona presents,
/// shaped after the standard descriptor set so a profile can be authored
/// directly from a real device's descriptor dump.
///
/// <para>This is what makes a profile a USB/IP persona: a profile carries
/// one, and only the USB/IP backend is in a position to present interfaces
/// Windows did not compose. The HID report descriptor is authored
/// alongside it for the interfaces that are HID.</para>
///
/// <para>This is a data model, not a driver. The backend reads it at
/// create time to serve the descriptors and to route the interfaces.</para></summary>
public sealed class UsbConfigurationSpec
{
    /// <summary>bConfigurationValue, the value SET_CONFIGURATION selects.
    /// One on every device in scope.</summary>
    [JsonPropertyName("configurationValue")]
    public byte ConfigurationValue { get; set; } = 1;

    /// <summary>bmAttributes. 0xC0 is self-powered with no remote wakeup,
    /// which is what both Sony pads report over USB.</summary>
    [JsonPropertyName("attributes")]
    public byte Attributes { get; set; } = 0xC0;

    /// <summary>Bus current in milliamps, as the device reports it.
    /// bMaxPower carries half this value, so 500 mA is encoded 0xFA.</summary>
    [JsonPropertyName("maxPowerMilliamps")]
    public int MaxPowerMilliamps { get; set; } = 500;

    /// <summary>The interfaces in bInterfaceNumber order. A DualSense has
    /// four: Audio Control, two Audio Streaming, and HID.</summary>
    [JsonPropertyName("interfaces")]
    public List<UsbInterfaceSpec> Interfaces { get; set; } = new();

    /// <summary>The bus speed the real device enumerates at:
    /// <c>"high"</c> (DualSense) or <c>"full"</c> (DualShock 4). Reported
    /// in the USB/IP import reply; usbip-win2 patches a full-speed
    /// device's endpoint intervals for its high-speed UDE presentation
    /// itself, so the blobs stay verbatim either way. A full-speed-only
    /// device also stalls Device_Qualifier and Other_Speed requests, as
    /// the real pad does.</summary>
    [JsonPropertyName("busSpeed")]
    public string BusSpeed { get; set; } = "high";

    /// <summary>The 18-byte device descriptor, hex, verbatim from a real
    /// pad. The backend serves this blob for GET_DESCRIPTOR(Device); the
    /// profile's vid/pid/versionNumber fields never override it, because
    /// the dump is the ground truth and a divergence between the two is a
    /// profile-authoring bug the create-path guard rejects.</summary>
    [JsonPropertyName("deviceDescriptor")]
    public string? DeviceDescriptorHex { get; set; }

    /// <summary>The full configuration descriptor the device serves at its
    /// operating speed (config header + every interface, class-specific and
    /// endpoint descriptor), hex, verbatim from a real pad. This blob IS
    /// the wire truth; the structured <see cref="Interfaces"/> model above
    /// describes the same configuration semantically so the backend can
    /// route without re-deriving meaning from bytes. The backend
    /// cross-checks the two at create time and refuses on mismatch.</summary>
    [JsonPropertyName("configurationDescriptor")]
    public string? ConfigurationDescriptorHex { get; set; }

    /// <summary>GET_DESCRIPTOR(Other_Speed_Configuration) blob, hex,
    /// for dual-speed devices. The DualSense's full-speed variant differs
    /// from the high-speed one (isochronous bInterval 1, a 1-channel
    /// microphone input terminal, a sampling-frequency control bit), so it
    /// cannot be synthesized from the high-speed blob.</summary>
    [JsonPropertyName("otherSpeedConfigurationDescriptor")]
    public string? OtherSpeedConfigurationDescriptorHex { get; set; }

    /// <summary>The UAC1 feature-unit control ranges the device answers on
    /// EP0, captured from a real pad's wire exchanges rather than invented.
    /// usbaudio.sys reads these during endpoint creation and maps the
    /// Windows volume slider across MIN..MAX.</summary>
    [JsonPropertyName("audioControls")]
    public List<UsbAudioControlSpec>? AudioControls { get; set; }

    /// <summary>Raw input report size in bytes for <b>vendor-class</b>
    /// (non-HID) interfaces. When the configuration has no HID interface the
    /// shared-memory DATA section carries opaque device bytes (e.g. the
    /// 20-byte Xbox 360 wired input report) with no Report ID, and the
    /// backend transmits exactly this many bytes verbatim on the interface's
    /// interrupt IN endpoint. Ignored for HID interfaces, where the report
    /// size comes from the HID descriptor.</summary>
    [JsonPropertyName("inputReportSize")]
    public int? InputReportSize { get; set; }

    /// <summary>Vendor-class EP0 requests the device answers. Each entry is
    /// a <c>(requestType, request, value, index) → response-hex</c> rule, so
    /// a profile can serve device-specific control reads (e.g. the Xbox 360
    /// <c>0xC1 / 0x01 / 0x0100</c> capability report) with no backend code
    /// per device. This is the extensibility seam for future non-HID
    /// controllers: add a profile with the right descriptor + vendorRequests
    /// and the USB/IP backend serves it.</summary>
    [JsonPropertyName("vendorRequests")]
    public List<VendorControlRequest>? VendorRequests { get; set; }

    /// <summary>True when this vendor-class persona speaks Microsoft's
    /// Gaming Input Protocol (GIP) over the data interface's interrupt
    /// endpoints instead of an opaque byte stream. The Xbox One controller
    /// (VID 0x045E) is the only GIP persona: the USB/IP device emulator
    /// drives the announce/identify/status/input handshake and decodes the
    /// host's power/LED/rumble/identify commands itself, so the shared
    /// DATA section carries a GIP input-payload body rather than a
    /// ready-to-send wire report.</summary>
    [JsonPropertyName("gip")]
    public bool Gip { get; set; }

    /// <summary>HID "Enhanced Wheel Support" resolution multiplier, when the
    /// profile's report descriptor declares a Resolution Multiplier feature
    /// (the high-resolution mouse). Its feature report has no report ID. The
    /// backend answers GET_REPORT(Feature, 0) with the current value and
    /// stores SET_REPORT(Feature, 0) writes; null for every profile without
    /// the feature.</summary>
    [JsonPropertyName("resolutionMultiplier")]
    public int? ResolutionMultiplier { get; set; }
}

/// <summary>One vendor-class EP0 rule served by the USB/IP backend. The
/// fields mirror the eight setup-packet bytes; <see cref="Response"/> is the
/// hex of the data stage the device returns to the host.</summary>
public sealed class VendorControlRequest
{
    /// <summary>bmRequestType, e.g. 0xC1 (vendor, interface, device→host).</summary>
    [JsonPropertyName("requestType")]
    public byte RequestType { get; set; }

    /// <summary>bRequest, e.g. 0x01.</summary>
    [JsonPropertyName("request")]
    public byte Request { get; set; }

    /// <summary>wValue (host byte order).</summary>
    [JsonPropertyName("value")]
    public ushort Value { get; set; }

    /// <summary>wIndex (host byte order). For an interface recipient this is
    /// the interface number.</summary>
    [JsonPropertyName("index")]
    public ushort Index { get; set; }

    /// <summary>Hex of the data the device returns for a device→host request.
    /// For a host→device request this is ignored.</summary>
    [JsonPropertyName("response")]
    public string? Response { get; set; }
}

/// <summary>One UAC1 feature unit's control state: the mute and volume
/// controls its bmaControls advertise, with the raw wire values a real pad
/// returns for GET_MIN / GET_MAX / GET_RES / GET_CUR. Volume values are
/// UAC1 s16 in 1/256 dB units (-25600 = -100 dB).</summary>
public sealed class UsbAudioControlSpec
{
    /// <summary>bUnitID of the feature unit (wIndex high byte of the
    /// class request addresses it).</summary>
    [JsonPropertyName("unitId")]
    public byte UnitId { get; set; }

    /// <summary>The Audio Control interface number the requests arrive on
    /// (wIndex low byte).</summary>
    [JsonPropertyName("controlInterface")]
    public byte ControlInterface { get; set; }

    /// <summary>What the unit controls, for the SDK surface:
    /// <c>"speaker"</c> (the render path feature unit) or
    /// <c>"microphone"</c> (the capture path).</summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = "";

    /// <summary>Boot-state CUR for the Mute control (0 or 1).</summary>
    [JsonPropertyName("muteCur")]
    public byte MuteCur { get; set; }

    /// <summary>GET_MIN(Volume) raw s16.</summary>
    [JsonPropertyName("volumeMinRaw")]
    public short VolumeMinRaw { get; set; }

    /// <summary>GET_MAX(Volume) raw s16.</summary>
    [JsonPropertyName("volumeMaxRaw")]
    public short VolumeMaxRaw { get; set; }

    /// <summary>GET_RES(Volume) raw s16.</summary>
    [JsonPropertyName("volumeResRaw")]
    public short VolumeResRaw { get; set; }

    /// <summary>Boot-state GET_CUR(Volume) raw s16.</summary>
    [JsonPropertyName("volumeCurRaw")]
    public short VolumeCurRaw { get; set; }
}

/// <summary>One interface, with every alternate setting it offers. USB
/// Audio Class streaming interfaces always carry at least two: alt 0 with
/// no endpoint (the zero-bandwidth setting the host selects when the
/// stream is idle) and alt 1 with the isochronous endpoint.</summary>
public sealed class UsbInterfaceSpec
{
    /// <summary>bInterfaceNumber.</summary>
    [JsonPropertyName("interfaceNumber")]
    public byte InterfaceNumber { get; set; }

    /// <summary>What this interface is, for the backend's routing rather
    /// than for the wire: <c>"hid"</c>, <c>"audioControl"</c>,
    /// <c>"audioStreamingOut"</c>, <c>"audioStreamingIn"</c>. The wire
    /// values live in <see cref="UsbAltSettingSpec"/>. The HID interface
    /// keeps serving the profile's existing report descriptor and codec
    /// unchanged, which is why a composite persona reuses everything the
    /// profile already declares.</summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = "";

    /// <summary>Alternate settings in bAlternateSetting order.</summary>
    [JsonPropertyName("altSettings")]
    public List<UsbAltSettingSpec> AltSettings { get; set; } = new();
}

/// <summary>One alternate setting: the interface descriptor's class
/// triple, its endpoints, and the class-specific descriptors that follow
/// it verbatim on the wire.</summary>
public sealed class UsbAltSettingSpec
{
    /// <summary>bAlternateSetting.</summary>
    [JsonPropertyName("altSetting")]
    public byte AltSetting { get; set; }

    /// <summary>bInterfaceClass. 0x01 Audio, 0x03 HID.</summary>
    [JsonPropertyName("interfaceClass")]
    public byte InterfaceClass { get; set; }

    /// <summary>bInterfaceSubClass. Under class 0x01: 0x01 Audio Control,
    /// 0x02 Audio Streaming.</summary>
    [JsonPropertyName("interfaceSubClass")]
    public byte InterfaceSubClass { get; set; }

    /// <summary>bInterfaceProtocol.</summary>
    [JsonPropertyName("interfaceProtocol")]
    public byte InterfaceProtocol { get; set; }

    /// <summary>Class-specific descriptors that follow the interface
    /// descriptor, as a hex string of the exact bytes. For Audio Control
    /// this is the whole topology (header, input and output terminals,
    /// feature units); for Audio Streaming it is the AS general descriptor
    /// plus the format type. Carried verbatim rather than modeled field by
    /// field, because the backend's job is to reproduce a real device's
    /// bytes, and a dump is the ground truth.</summary>
    [JsonPropertyName("classDescriptors")]
    public string? ClassDescriptors { get; set; }

    /// <summary>Endpoints this setting exposes. Empty on an Audio Control
    /// interface and on every zero-bandwidth alt 0.</summary>
    [JsonPropertyName("endpoints")]
    public List<UsbEndpointSpec> Endpoints { get; set; } = new();

    /// <summary>For an audio streaming setting, what the stream carries.
    /// Null on HID and Audio Control interfaces.</summary>
    [JsonPropertyName("audioStream")]
    public UsbAudioStreamSpec? AudioStream { get; set; }
}

/// <summary>One endpoint descriptor.</summary>
public sealed class UsbEndpointSpec
{
    /// <summary>bEndpointAddress, direction bit included. 0x01 is OUT
    /// endpoint 1; 0x82 is IN endpoint 2.</summary>
    [JsonPropertyName("address")]
    public byte Address { get; set; }

    /// <summary>Transfer type: <c>"isochronous"</c> or
    /// <c>"interrupt"</c>.</summary>
    [JsonPropertyName("transferType")]
    public string TransferType { get; set; } = "";

    /// <summary>Synchronisation type for isochronous endpoints:
    /// <c>"adaptive"</c> on the Sony OUT stream, <c>"asynchronous"</c> on
    /// the IN stream.</summary>
    [JsonPropertyName("syncType")]
    public string? SyncType { get; set; }

    /// <summary>wMaxPacketSize. This is the per-interval byte budget the
    /// backend must produce or consume, so it sets the buffer sizes on
    /// the audio surfaces.</summary>
    [JsonPropertyName("maxPacketSize")]
    public int MaxPacketSize { get; set; }

    /// <summary>bInterval. At high speed the service interval is
    /// 2^(bInterval-1) microframes, so 4 means 8 microframes, 1 ms. This
    /// is the cadence the pacing spike measured.</summary>
    [JsonPropertyName("interval")]
    public byte Interval { get; set; }

    /// <summary>Class-specific endpoint descriptor bytes, hex, appended
    /// after the endpoint descriptor.</summary>
    [JsonPropertyName("classDescriptors")]
    public string? ClassDescriptors { get; set; }
}

/// <summary>The PCM format an audio streaming alt setting carries, plus
/// what the channels mean, which is the part the backend routes on.</summary>
public sealed class UsbAudioStreamSpec
{
    /// <summary>Channel count. Four on the DualSense OUT stream: two
    /// speaker plus two voice-coil actuators.</summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    /// <summary>Bits per sample.</summary>
    [JsonPropertyName("bitsPerSample")]
    public int BitsPerSample { get; set; }

    /// <summary>Sample rate in Hz.</summary>
    [JsonPropertyName("sampleRateHz")]
    public int SampleRateHz { get; set; }

    /// <summary>wChannelConfig from the input terminal, verbatim. The
    /// DualSense reports 0x0033: Left Front, Right Front, Left Surround,
    /// Right Surround.</summary>
    [JsonPropertyName("channelConfig")]
    public int ChannelConfig { get; set; }

    /// <summary>What each channel is for, in channel order, so a consumer
    /// can address the haptic actuators without decoding terminal
    /// topology: <c>"speakerLeft"</c>, <c>"speakerRight"</c>,
    /// <c>"hapticLeft"</c>, <c>"hapticRight"</c>, <c>"microphone"</c>.
    /// This is GKMD's semantic layer, not a USB field. It is what
    /// makes the four-channel stream usable rather than merely
    /// present.</summary>
    [JsonPropertyName("channelRoles")]
    public List<string> ChannelRoles { get; set; } = new();
}
