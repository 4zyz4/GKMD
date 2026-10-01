using System;
using System.Collections.Generic;
using System.Linq;
using GKMD.Internal;

namespace GKMD;

internal static class StaticProfileRegistry
{
    private static readonly Dictionary<string, ControllerProfile> s_profiles = new(StringComparer.Ordinal);

    static StaticProfileRegistry()
    {
        s_profiles["xbox360"] = BuildXbox360();
        s_profiles["dualsense"] = BuildDualSense();
        s_profiles["dualshock-4-v2"] = BuildDualShock4();
        s_profiles["switch-pro"] = BuildSwitchPro();
        s_profiles["joycon-l"] = BuildJoycon(
            id: "joycon-l",
            pid: "0x2006",
            product: "Joy-Con (L)",
            serial: "000000000001");
        s_profiles["joycon-r"] = BuildJoycon(
            id: "joycon-r",
            pid: "0x2007",
            product: "Joy-Con (R)",
            serial: "000000000002");
        s_profiles["keyboard"] = BuildKeyboard();
        s_profiles["mouse"] = BuildMouse();
        s_profiles["xbox-one"] = BuildXboxOne();
    }

    public static Dictionary<string, ControllerProfile> AllProfiles => s_profiles;

    internal static ControllerProfile? GetProfile(string id) => s_profiles.TryGetValue(id, out var p) ? p : null;

    private static ControllerProfile BuildXbox360()
    {
        var uc = new UsbConfigurationSpec
        {
            ConfigurationValue = 1,
            Attributes = 0xC0,
            MaxPowerMilliamps = 500,
            BusSpeed = "full",
            DeviceDescriptorHex = "12010002FFFFFF085E048E02140101020301",
            ConfigurationDescriptorHex = "09029900040100C0FA0904000002FF5D0100112100010125811400000000130108000007058103200004070501032000080904010004FF5D03001B2100010101824001022016830000000000001603000000000000070582032000020705020320000407058303200040070503032000100904020001FF5D0200092100010122840700070584032000100904030000FFFD1304064100010103",
            InputReportSize = 20,
            VendorRequests = new List<VendorControlRequest>
            {
                new VendorControlRequest { RequestType = 193, Request = 1, Value = 256, Index = 0, Response = "0014FFFFFFFFFF7FFF7FFF7FFF7FFFFFFFFF0000" }
            },
            Interfaces = new List<UsbInterfaceSpec>
            {
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 0, Function = "",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 2, InterfaceSubClass = 0xFF, InterfaceProtocol = 0x5D,
                            ClassDescriptors = "0011210001012581140000000013010800000705810320000407050103200008",
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x81, TransferType = "interrupt", MaxPacketSize = 32, Interval = 4 },
                                new UsbEndpointSpec { Address = 0x01, TransferType = "interrupt", MaxPacketSize = 32, Interval = 8 }
                            }
                        }
                    }
                },
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 1, Function = "",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 4, InterfaceSubClass = 0xFF, InterfaceProtocol = 0x5D,
                            ClassDescriptors = "001B2100010101824001022016",
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x82, TransferType = "interrupt", MaxPacketSize = 32, Interval = 2 },
                                new UsbEndpointSpec { Address = 0x02, TransferType = "interrupt", MaxPacketSize = 32, Interval = 4 }
                            }
                        }
                    }
                },
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 2, Function = "",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 0xFF, InterfaceProtocol = 0x5D,
                            ClassDescriptors = "001603000000000000001603000000000000",
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x83, TransferType = "interrupt", MaxPacketSize = 32, Interval = 64 },
                                new UsbEndpointSpec { Address = 0x03, TransferType = "interrupt", MaxPacketSize = 32, Interval = 16 }
                            }
                        }
                    }
                },
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 3, Function = "",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 0, InterfaceSubClass = 0xFF, InterfaceProtocol = 0xFD,
                            ClassDescriptors = "0009210001012284070007058403200010",
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x84, TransferType = "interrupt", MaxPacketSize = 32, Interval = 16 }
                            }
                        }
                    }
                }
            }
        };

        return new ControllerProfile
        {
            Id = "xbox360",
            Name = "Xbox 360 Controller (Wired) — Full",
            Vendor = "Microsoft",
            Vid = "0x045E",
            Pid = "0x028E",
            ProductString = "Xbox 360 Controller for Windows",
            ManufacturerString = "Microsoft",
            Type = "gamepad",
            InputReportSize = 20,
            Notes = "Wired Xbox 360 controller as synthesized by usbip-win. Ported from VIIPER's device/xbox360 device.go: a full-speed, vendor-class (bDeviceClass 0xFF) composite with four 0xFF interfaces, 0x21/0x41 class descriptors and a 20-byte opaque input report. No HID report descriptor; input is delivered verbatim from shared memory as 20 raw bytes (RID-less). The only control request is the 0xC1/0x01/0x0100 capability report (handled via the vendorRequests table). Rum/LED output arrives on EP0x01 OUT and is surfaced through the shared-memory output ring.",
            UsbConfiguration = uc
        };
    }

    // ── Xbox One (Model 1708) GIP persona ────────────────────────────────
    //
    // The wired Xbox One controller is a vendor-class USB device
    // (bDeviceClass 0xFF, bInterfaceSubClass 0x47, bDeviceProtocol 0xD0)
    // whose real protocol is Microsoft's Gaming Input Protocol (GIP), not
    // HID. Windows binds it through dc1-controller.inf (XboxComposite) and
    // xboxgip.sys, and turns the GIP attachments into HID children that
    // xinputhid.sys presents as XInput. The USB/IP device emulator owns the
    // GIP state machine (GipResponder); this profile only declares the
    // descriptor and the routing metadata.
    //
    // Descriptor shape (single data interface, no audio function): the data
    // interface is class 0xFF/0x47/0xD0 with an interrupt IN (0x81) and
    // interrupt OUT (0x01), both 64-byte, which is exactly what xone's
    // wired transport and Windows' xboxgip expect. Audio (interface 1) is
    // omitted: it is optional for both drivers and third-party Xbox One
    // pads ship without it.

    private static ControllerProfile BuildXboxOne()
    {
        var uc = new UsbConfigurationSpec
        {
            ConfigurationValue = 1,
            Attributes = 0x80,
            MaxPowerMilliamps = 500,
            BusSpeed = "full",
            Gip = true,
            // bcdUSB 2.00, class 0xFF, subclass 0x47, protocol 0xD0,
            // bMaxPacketSize0 64, VID 045E PID 02EA, bcdDevice 0x0501,
            // iMfr 1 / iProduct 2 / iSerial 3.
            DeviceDescriptorHex = "12010002FF47D0405E04EA02010501020301",
            // Config wTotalLength 0x20: one vendor-class interface with an
            // interrupt IN 0x81 and interrupt OUT 0x01, 64-byte packets.
            ConfigurationDescriptorHex =
                "0902200001010080FA" +
                "0904000002FF47D000" +
                "07058103400004" +
                "07050103400004",
            InputReportSize = 18,
            Interfaces = new List<UsbInterfaceSpec>
            {
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 0, Function = "",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 0xFF, InterfaceSubClass = 0x47,
                            InterfaceProtocol = 0xD0,
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x81, TransferType = "interrupt", MaxPacketSize = 64, Interval = 4 },
                                new UsbEndpointSpec { Address = 0x01, TransferType = "interrupt", MaxPacketSize = 64, Interval = 4 }
                            }
                        }
                    }
                }
            }
        };

        return new ControllerProfile
        {
            Id = "xbox-one",
            Name = "Xbox One Controller (Model 1708)",
            Vendor = "Microsoft",
            Vid = "0x045E",
            Pid = "0x02EA",
            ProductString = "Controller",
            ManufacturerString = "Microsoft",
            Type = "gamepad",
            InputReportSize = 18,
            Notes = "Wired Xbox One controller (Model 1708, VID 045E:02EA) as a " +
                    "genuine vendor-class GIP device. Windows binds dc1-controller.inf " +
                    "(xboxgip.sys) and the GIP attachment becomes a HID child that " +
                    "xinputhid.sys exposes as XInput. The GIP announce/identify/status/" +
                    "input handshake and the host's power/LED/rumble commands are " +
                    "implemented by UsbipEmulatedDevice's GipResponder. Audio (the " +
                    "controller's headset function) is not presented, matching " +
                    "third-party pads.",
            UsbConfiguration = uc
        };
    }

    private static ControllerProfile BuildDualSense()
    {
        var ds = new ControllerProfile
        {
            Id = "dualsense",
            Name = "DualSense (PS5) — Full",
            Vendor = "Sony",
            Vid = "0x054C",
            Pid = "0x0CE6",
            ProductString = "DualSense Wireless Controller",
            ManufacturerString = "Sony Interactive Entertainment",
            Type = "gamepad",
            Descriptor = "05010905a1018501093009310932093509330934150026ff007508950681020600ff09209501810205010939150025073500463b016514750495018142650005091901290f150025017501950f81020600ff0921950d81020600ff0922150026ff0075089534810285020923952f9102850509339528b10285080934952fb102850909249513b102850a0925951ab10285200926953fb102852109279504b10285220940953fb10285800928953fb10285810929953fb1028582092a9509b1028583092b953fb1028584092c953fb1028585092d9502b10285a0092e9501b10285e0092f953fb10285f00930953fb10285f10931953fb10285f20932950fb10285f40935953fb10285f509369503b102c0",
            InputReportSize = 64,
            ButtonMap = new[] { 1, 2, 0, 3, 4, 5, 8, 9, 10, 11, 12, 13, -1, -1, -1, 14 },
            TriggerButtons = new[] { 6, 7 },
            AxisMap = new Dictionary<string, string>
            {
                { "0x32", "rightStickX" },
                { "0x35", "rightStickY" },
                { "0x33", "leftTrigger" },
                { "0x34", "rightTrigger" }
            },
            Notes = "Issue #39: the four-interface USB composite a real DualSense presents, authored from DJm00n/ControllersInfo DescriptorDump_Wireless_Controller (DualSense Model CFI-ZCT1W): configuration wTotalLength 227, bNumInterfaces 4, self-powered, 500 mA. Interface 3 is the HID function; the report descriptor, codec, buttonMap, axisMap and extendedReport blocks below describe it. Interfaces 0-2 are USB Audio Class: the OUT stream carries 4 channels at 48 kHz (channels 1/2 speaker, 3/4 voice-coil haptics, wChannelConfig 0x0033) and the IN stream carries the 2-channel headset microphone. Uses the USB/IP create path. The transport ships inside GKMD.Core.dll and deploys itself on first use, so this profile needs nothing installed by hand.",
            ExtendedReport = new ExtendedReportSpec
            {
                ReportId = "0x01",
                Size = 64,
                AlwaysArmed = true,
                Fields = new List<FieldSpec>
                {
                    new FieldSpec { Byte = 1, Type = "uint8-axis", Semantic = "leftStickX", Center = 128 },
                    new FieldSpec { Byte = 2, Type = "uint8-axis", Semantic = "leftStickY", Center = 128 },
                    new FieldSpec { Byte = 3, Type = "uint8-axis", Semantic = "rightStickX", Center = 128 },
                    new FieldSpec { Byte = 4, Type = "uint8-axis", Semantic = "rightStickY", Center = 128 },
                    new FieldSpec { Byte = 5, Type = "uint8-trigger", Semantic = "leftTrigger" },
                    new FieldSpec { Byte = 6, Type = "uint8-trigger", Semantic = "rightTrigger" },
                    new FieldSpec { Byte = 7, Type = "uint8-rolling", Semantic = "sequenceNum", Initial = 0 },
                    new FieldSpec { Byte = 8, Bits = "0-3", Type = "hat-octant", Semantic = "hat", NeutralValue = 8 },
                    new FieldSpec { Byte = 8, Bits = "4-7", Type = "button-mask", Buttons = new List<string> { "X", "A", "B", "Y" } },
                    new FieldSpec { Byte = 9, Type = "button-mask", Buttons = new List<string> { "LeftBumper", "RightBumper", "LT_DIGITAL", "RT_DIGITAL", "Back", "Start", "LeftStick", "RightStick" } },
                    new FieldSpec { Byte = 10, Type = "button-mask", Buttons = new List<string> { "Guide", "Touchpad", "Misc1" } },
                    new FieldSpec { Byte = 16, Type = "int16-le", Semantic = "gyroPitch" },
                    new FieldSpec { Byte = 18, Type = "int16-le", Semantic = "gyroYaw" },
                    new FieldSpec { Byte = 20, Type = "int16-le", Semantic = "gyroRoll" },
                    new FieldSpec { Byte = 22, Type = "int16-le", Semantic = "accelX" },
                    new FieldSpec { Byte = 24, Type = "int16-le", Semantic = "accelY" },
                    new FieldSpec { Byte = 26, Type = "int16-le", Semantic = "accelZ" },
                    new FieldSpec { Byte = 28, Type = "uint32-le", Semantic = "sensorTimestamp" },
                    new FieldSpec { Byte = 33, Type = "touchpad-finger", Semantic = "touchpadFinger0" },
                    new FieldSpec { Byte = 37, Type = "touchpad-finger", Semantic = "touchpadFinger1" },
                    new FieldSpec { Byte = 53, Bits = "0-3", Type = "uint8-battery", Semantic = "batteryLevel" },
                    new FieldSpec { Byte = 53, Bits = "4-7", Type = "bitfield", Buttons = new List<string> { "batteryCharging", "batteryFull" } }
                }
            },
            ExtendedOutputReport = new ExtendedReportSpec
            {
                ReportId = "0x02",
                Size = 48,
                Fields = new List<FieldSpec>
                {
                    new FieldSpec { Byte = 1, Type = "uint8", Semantic = "validFlag0" },
                    new FieldSpec { Byte = 2, Type = "uint8", Semantic = "validFlag1" },
                    new FieldSpec { Byte = 3, Type = "uint8", Semantic = "rightMotor" },
                    new FieldSpec { Byte = 4, Type = "uint8", Semantic = "leftMotor" },
                    new FieldSpec { Byte = 5, Type = "uint8", Semantic = "headphoneVolume" },
                    new FieldSpec { Byte = 6, Type = "uint8", Semantic = "speakerVolume" },
                    new FieldSpec { Byte = 7, Type = "uint8", Semantic = "micVolume" },
                    new FieldSpec { Byte = 8, Type = "uint8", Semantic = "audioControlFlags" },
                    new FieldSpec { Byte = 9, Type = "uint8", Semantic = "muteLed" },
                    new FieldSpec { Bytes = "11-21", Type = "bytes-passthrough", Semantic = "rightTriggerEffect" },
                    new FieldSpec { Bytes = "22-32", Type = "bytes-passthrough", Semantic = "leftTriggerEffect" },
                    new FieldSpec { Byte = 39, Type = "uint8", Semantic = "validFlag2" },
                    new FieldSpec { Byte = 42, Type = "uint8", Semantic = "lightbarSetup" },
                    new FieldSpec { Byte = 43, Type = "uint8", Semantic = "ledBrightness" },
                    new FieldSpec { Byte = 44, Type = "uint8", Semantic = "playerIndicator" },
                    new FieldSpec { Bytes = "45-47", Type = "rgb24", Semantic = "lightbar" },
                    new FieldSpec { Bytes = "1-47", Type = "bytes-passthrough", Semantic = "effectPayload" }
                }
            },
            Layout = new GKGamepadLayout
            {
                Source = "https://en.wikipedia.org/wiki/DualShock",
                Sticks = new List<GKStick>
                {
                    new GKStick { Side = GKStickSide.Left, XAxis = GKAxis.X, YAxis = GKAxis.Y, ClickButton = 12 },
                    new GKStick { Side = GKStickSide.Right, XAxis = GKAxis.Z, YAxis = GKAxis.Rz, ClickButton = 13 }
                },
                Triggers = new List<GKTrigger>
                {
                    new GKTrigger { Axis = GKAxis.Rx, Side = GKTriggerSide.Left, Kind = GKTriggerKind.Analog },
                    new GKTrigger { Axis = GKAxis.Ry, Side = GKTriggerSide.Right, Kind = GKTriggerKind.Analog }
                },
                Dpad = new GKDpad { Encoding = GKDpadEncoding.Hat, HatAxis = GKAxis.Hat, HatPositions = 8 },
                FaceButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.FaceCross, ButtonIndex = 0 },
                    new GKButtonBinding { Role = GKButtonRole.FaceCircle, ButtonIndex = 1 },
                    new GKButtonBinding { Role = GKButtonRole.FaceSquare, ButtonIndex = 2 },
                    new GKButtonBinding { Role = GKButtonRole.FaceTriangle, ButtonIndex = 3 }
                },
                ShoulderButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.LeftBumper, ButtonIndex = 4 },
                    new GKButtonBinding { Role = GKButtonRole.RightBumper, ButtonIndex = 5 },
                    new GKButtonBinding { Role = GKButtonRole.LeftTriggerClick, ButtonIndex = 6 },
                    new GKButtonBinding { Role = GKButtonRole.RightTriggerClick, ButtonIndex = 7 }
                },
                SystemButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.Share, ButtonIndex = 8 },
                    new GKButtonBinding { Role = GKButtonRole.Options, ButtonIndex = 9 },
                    new GKButtonBinding { Role = GKButtonRole.Ps, ButtonIndex = 10 },
                    new GKButtonBinding { Role = GKButtonRole.Guide, ButtonIndex = 11 }
                },
                ExtraButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.Mute, ButtonIndex = 14 }
                },
                Haptics = new GKHaptics { Rumble = GKRumbleKind.VoiceCoilHaptic, TriggerHaptics = true },
                Imu = new GKImu { Accelerometer = true, Gyroscope = true, Magnetometer = false }
            },
            UsbConfiguration = new UsbConfigurationSpec
            {
                ConfigurationValue = 1,
                Attributes = 192,
                MaxPowerMilliamps = 500,
                BusSpeed = "high",
                DeviceDescriptorHex = "12010002000000404c05e60c000101020001",
                ConfigurationDescriptorHex = "0902e300040100c0fa0904000000010100000a2401000149000201020c24020101010604330000000c24060201010300000000000924030301030402000c2402040204030203000000092406050401030000092403060101010500090401000001020000090401010101020000072401010101000b2402010402100180bb0009050109880104000007250100000000090402000001020000090402010101020000072401060101000b2402010202100180bb0009058205c400040000072501000000000904030002030000000921110100012211010705840340000607050303400006",
                OtherSpeedConfigurationDescriptorHex = "0902e300040100c0fa0904000000010100000a2401000149000201020c24020101010604330000000c24060201010300000000000924030301030402000c2402040204030203000000092406050401030000092403060101010500090401000001020000090401010101020000072401010101000b2402010402100180bb0009050109880104000007250100000000090402000001020000090402010101020000072401060101000b2402010202100180bb0009058205c400040000072501000000000904030002030000000921110100012211010705840340000607050303400006",
                AudioControls = new List<UsbAudioControlSpec>
                {
                    new UsbAudioControlSpec { UnitId = 2, ControlInterface = 0, Function = "speaker", MuteCur = 0, VolumeMinRaw = -25600, VolumeMaxRaw = 0, VolumeResRaw = 256, VolumeCurRaw = -25600 },
                    new UsbAudioControlSpec { UnitId = 5, ControlInterface = 0, Function = "microphone", MuteCur = 0, VolumeMinRaw = 0, VolumeMaxRaw = 12288, VolumeResRaw = 122, VolumeCurRaw = 3809 }
                },
                Interfaces = new List<UsbInterfaceSpec>
                {
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 0, Function = "audioControl",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec { AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 1, InterfaceProtocol = 0 }
                        }
                    },
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 1, Function = "audioStreamingOut",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec { AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0 },
                            new UsbAltSettingSpec
                            {
                                AltSetting = 1, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0,
                                Endpoints = new List<UsbEndpointSpec>
                                {
                                    new UsbEndpointSpec { Address = 1, TransferType = "isochronous", SyncType = "adaptive", MaxPacketSize = 392, Interval = 4 }
                                },
                                AudioStream = new UsbAudioStreamSpec { Channels = 4, BitsPerSample = 16, SampleRateHz = 48000, ChannelConfig = 51, ChannelRoles = new List<string> { "speakerLeft", "speakerRight", "hapticLeft", "hapticRight" } }
                            }
                        }
                    },
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 2, Function = "audioStreamingIn",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec { AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0 },
                            new UsbAltSettingSpec
                            {
                                AltSetting = 1, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0,
                                Endpoints = new List<UsbEndpointSpec>
                                {
                                    new UsbEndpointSpec { Address = 130, TransferType = "isochronous", SyncType = "asynchronous", MaxPacketSize = 196, Interval = 4 }
                                },
                                AudioStream = new UsbAudioStreamSpec { Channels = 2, BitsPerSample = 16, SampleRateHz = 48000, ChannelConfig = 3, ChannelRoles = new List<string> { "microphone", "microphone" } }
                            }
                        }
                    },
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 3, Function = "hid",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec
                            {
                                AltSetting = 0, InterfaceClass = 3, InterfaceSubClass = 0, InterfaceProtocol = 0,
                                Endpoints = new List<UsbEndpointSpec>
                                {
                                    new UsbEndpointSpec { Address = 132, TransferType = "interrupt", MaxPacketSize = 64, Interval = 6 },
                                    new UsbEndpointSpec { Address = 3, TransferType = "interrupt", MaxPacketSize = 64, Interval = 6 }
                                }
                            }
                        }
                    }
                }
            }
        };

        return ds;
    }

    private static ControllerProfile BuildDualShock4()
    {
        return new ControllerProfile
        {
            Id = "dualshock-4-v2",
            Name = "DualShock 4 v2 (CUH-ZCT2) — Full",
            Vendor = "Sony",
            Vid = "0x054C",
            Pid = "0x09CC",
            ProductString = "Wireless Controller",
            ManufacturerString = "Sony Interactive Entertainment",
            SerialNumberString = "JDM-050",
            Type = "gamepad",
            Descriptor = "05010905a10185010930093109320935150026ff007508950481020939150025073500463b016514750495018142650005091901290e150025017501950e81020600ff0920750695011500257f8102050109330934150026ff007508950281020600ff09219536810285050922951f9102850409239524b102850209249524b102850809259503b102851009269504b102851109279502b10285120602ff0921950fb102851309229516b10285140605ff09209510b10285150921952cb1020680ff858009209506b102858109219506b102858209229505b102858309239501b102858409249504b102858509259506b102858609269506b102858709279523b10285880928953fb102858909299502b102859009309505b102859109319503b102859209329503b10285930933950cb10285940934953fb10285a009409506b10285a109419501b10285a209429501b10285a309439530b10285a40944950db10285f00947953fb10285f10948953fb10285f20949950fb10285a7094a9501b10285a8094b9501b10285a9094c9508b10285aa094e9501b10285ab094f9539b10285ac09509539b10285ad0951950bb10285ae09529501b10285af09539502b10285b00954953fb10285e009579502b10285b30955953fb10285b40955953fb10285b50956953fb10285d00958953fb10285d40959953fb102c0",
            InputReportSize = 64,
            ButtonMap = new[] { 1, 2, 0, 3, 4, 5, 8, 9, 10, 11, 12, 13, -1, -1, -1, -1 },
            TriggerButtons = new[] { 6, 7 },
            AxisMap = new Dictionary<string, string>
            {
                { "0x32", "rightStickX" },
                { "0x35", "rightStickY" },
                { "0x33", "leftTrigger" },
                { "0x34", "rightTrigger" }
            },
            Notes = "Issue #39: the four-interface USB composite a real DualShock 4 v2 presents, authored from DJm00n/ControllersInfo DescriptorDump_Wireless_Controller (DualShock4 Model CUH-ZCT2E) and the dualshock4.pcap wire capture: configuration wTotalLength 225, bNumInterfaces 4, self-powered, 500 mA, FULL-speed device. Interface 3 is the HID function (the 507-byte report descriptor below is verified against the capture), whose codec and inputs the profile carries. Interfaces 0-2 are USB Audio Class: both terminals are Headset (no speaker terminal, no 4-channel stream, so no haptics lane; this variant exists for fidelity and the microphone). OUT stream 2 ch / 16-bit / 32 kHz (wMaxPacketSize 132), IN stream 1 ch / 16-bit / 16 kHz (wMaxPacketSize 34). UAC control ranges are the real pad's wire values from the capture. Uses the USB/IP create path. The transport ships inside GKMD.Core.dll and deploys itself on first use, so this profile needs nothing installed by hand.",
            ExtendedReport = new ExtendedReportSpec
            {
                ReportId = "0x01",
                Size = 64,
                Fields = new List<FieldSpec>
                {
                    new FieldSpec { Byte = 1, Type = "uint8-axis", Semantic = "leftStickX", Center = 128 },
                    new FieldSpec { Byte = 2, Type = "uint8-axis", Semantic = "leftStickY", Center = 128 },
                    new FieldSpec { Byte = 3, Type = "uint8-axis", Semantic = "rightStickX", Center = 128 },
                    new FieldSpec { Byte = 4, Type = "uint8-axis", Semantic = "rightStickY", Center = 128 },
                    new FieldSpec { Byte = 5, Bits = "0-3", Type = "hat-octant", Semantic = "hat", NeutralValue = 8 },
                    new FieldSpec { Byte = 5, Bits = "4-7", Type = "button-mask", Buttons = new List<string> { "X", "A", "B", "Y" } },
                    new FieldSpec { Byte = 6, Type = "button-mask", Buttons = new List<string> { "LeftBumper", "RightBumper", "LT_DIGITAL", "RT_DIGITAL", "Back", "Start", "LeftStick", "RightStick" } },
                    new FieldSpec { Byte = 7, Type = "button-mask", Buttons = new List<string> { "Guide", "Touchpad" } },
                    new FieldSpec { Byte = 8, Type = "uint8-trigger", Semantic = "leftTrigger" },
                    new FieldSpec { Byte = 9, Type = "uint8-trigger", Semantic = "rightTrigger" },
                    new FieldSpec { Byte = 14, Type = "uint8-battery", Semantic = "batteryLevel" },
                    new FieldSpec { Byte = 15, Type = "int16-le", Semantic = "gyroPitch" },
                    new FieldSpec { Byte = 17, Type = "int16-le", Semantic = "gyroYaw" },
                    new FieldSpec { Byte = 19, Type = "int16-le", Semantic = "gyroRoll" },
                    new FieldSpec { Byte = 21, Type = "int16-le", Semantic = "accelX" },
                    new FieldSpec { Byte = 23, Type = "int16-le", Semantic = "accelY" },
                    new FieldSpec { Byte = 25, Type = "int16-le", Semantic = "accelZ" },
                    new FieldSpec { Byte = 33, Bits = "0-1", Type = "bitfield", Buttons = new List<string> { "headphonesConnected", "micMuted" } },
                    new FieldSpec { Byte = 38, Type = "touchpad-finger", Semantic = "touchpadFinger0" },
                    new FieldSpec { Byte = 42, Type = "touchpad-finger", Semantic = "touchpadFinger1" }
                }
            },
            ExtendedOutputReport = new ExtendedReportSpec
            {
                ReportId = "0x05",
                Size = 32,
                Fields = new List<FieldSpec>
                {
                    new FieldSpec { Byte = 1, Type = "uint8", Semantic = "validFlag0" },
                    new FieldSpec { Byte = 2, Type = "uint8", Semantic = "validFlag1" },
                    new FieldSpec { Byte = 4, Type = "uint8", Semantic = "rightMotor" },
                    new FieldSpec { Byte = 5, Type = "uint8", Semantic = "leftMotor" },
                    new FieldSpec { Bytes = "6-8", Type = "rgb24", Semantic = "lightbar" },
                    new FieldSpec { Byte = 9, Type = "uint8", Semantic = "flashOn" },
                    new FieldSpec { Byte = 10, Type = "uint8", Semantic = "flashOff" },
                    new FieldSpec { Bytes = "1-31", Type = "bytes-passthrough", Semantic = "effectPayload" },
                    new FieldSpec { Byte = 19, Type = "uint8", Semantic = "headphoneVolumeLeft" },
                    new FieldSpec { Byte = 20, Type = "uint8", Semantic = "headphoneVolumeRight" },
                    new FieldSpec { Byte = 21, Type = "uint8", Semantic = "micVolume" },
                    new FieldSpec { Byte = 22, Type = "uint8", Semantic = "speakerVolume" }
                }
            },
            Layout = new GKGamepadLayout
            {
                Source = "https://en.wikipedia.org/wiki/DualShock#DualShock_4",
                Sticks = new List<GKStick>
                {
                    new GKStick { Side = GKStickSide.Left, XAxis = GKAxis.X, YAxis = GKAxis.Y, ClickButton = 12 },
                    new GKStick { Side = GKStickSide.Right, XAxis = GKAxis.Z, YAxis = GKAxis.Rz, ClickButton = 13 }
                },
                Triggers = new List<GKTrigger>
                {
                    new GKTrigger { Axis = GKAxis.Rx, Side = GKTriggerSide.Left, Kind = GKTriggerKind.Analog },
                    new GKTrigger { Axis = GKAxis.Ry, Side = GKTriggerSide.Right, Kind = GKTriggerKind.Analog }
                },
                Dpad = new GKDpad { Encoding = GKDpadEncoding.Hat, HatAxis = GKAxis.Hat, HatPositions = 8 },
                FaceButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.FaceCross, ButtonIndex = 0 },
                    new GKButtonBinding { Role = GKButtonRole.FaceCircle, ButtonIndex = 1 },
                    new GKButtonBinding { Role = GKButtonRole.FaceSquare, ButtonIndex = 2 },
                    new GKButtonBinding { Role = GKButtonRole.FaceTriangle, ButtonIndex = 3 }
                },
                ShoulderButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.LeftBumper, ButtonIndex = 4 },
                    new GKButtonBinding { Role = GKButtonRole.RightBumper, ButtonIndex = 5 },
                    new GKButtonBinding { Role = GKButtonRole.LeftTriggerClick, ButtonIndex = 6 },
                    new GKButtonBinding { Role = GKButtonRole.RightTriggerClick, ButtonIndex = 7 }
                },
                SystemButtons = new List<GKButtonBinding>
                {
                    new GKButtonBinding { Role = GKButtonRole.Share, ButtonIndex = 8 },
                    new GKButtonBinding { Role = GKButtonRole.Options, ButtonIndex = 9 },
                    new GKButtonBinding { Role = GKButtonRole.Ps, ButtonIndex = 10 },
                    new GKButtonBinding { Role = GKButtonRole.Guide, ButtonIndex = 11 }
                },
                ExtraButtons = new List<GKButtonBinding>(),
                Haptics = new GKHaptics { Rumble = GKRumbleKind.DualErm, TriggerHaptics = false },
                Imu = new GKImu { Accelerometer = true, Gyroscope = true, Magnetometer = false }
            },
            UsbConfiguration = new UsbConfigurationSpec
            {
                ConfigurationValue = 1,
                Attributes = 192,
                MaxPowerMilliamps = 500,
                BusSpeed = "full",
                DeviceDescriptorHex = "12010002000000404c05cc09000101020301",
                ConfigurationDescriptorHex = "0902e100040100c0fa0904000000010100000a2401000147000201020c24020101010602030000000a2406020101030000000924030302040402000c2402040204030100000000092406050401030000092403060101010500090401000001020000090401010101020000072401010101000b24020102021001007d0009050109840001000007250100000000090402000001020000090402010101020000072401060101000b24020101021001803e000905820522000100000725010000000009040300020300000009211101000122fb010705840340000507050303400005",
                AudioControls = new List<UsbAudioControlSpec>
                {
                    new UsbAudioControlSpec { UnitId = 2, ControlInterface = 0, Function = "headset", MuteCur = 0, VolumeMinRaw = -18688, VolumeMaxRaw = -256, VolumeResRaw = 256, VolumeCurRaw = 1792 },
                    new UsbAudioControlSpec { UnitId = 5, ControlInterface = 0, Function = "microphone", MuteCur = 0, VolumeMinRaw = -5952, VolumeMaxRaw = 6144, VolumeResRaw = 192, VolumeCurRaw = -768 }
                },
                Interfaces = new List<UsbInterfaceSpec>
                {
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 0, Function = "audioControl",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec { AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 1, InterfaceProtocol = 0 }
                        }
                    },
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 1, Function = "audioStreamingOut",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec { AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0 },
                            new UsbAltSettingSpec
                            {
                                AltSetting = 1, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0,
                                Endpoints = new List<UsbEndpointSpec>
                                {
                                    new UsbEndpointSpec { Address = 1, TransferType = "isochronous", SyncType = "adaptive", MaxPacketSize = 132, Interval = 1 }
                                },
                                AudioStream = new UsbAudioStreamSpec { Channels = 2, BitsPerSample = 16, SampleRateHz = 32000, ChannelConfig = 3, ChannelRoles = new List<string> { "headsetLeft", "headsetRight" } }
                            }
                        }
                    },
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 2, Function = "audioStreamingIn",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec { AltSetting = 0, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0 },
                            new UsbAltSettingSpec
                            {
                                AltSetting = 1, InterfaceClass = 1, InterfaceSubClass = 2, InterfaceProtocol = 0,
                                Endpoints = new List<UsbEndpointSpec>
                                {
                                    new UsbEndpointSpec { Address = 130, TransferType = "isochronous", SyncType = "asynchronous", MaxPacketSize = 34, Interval = 1 }
                                },
                                AudioStream = new UsbAudioStreamSpec { Channels = 1, BitsPerSample = 16, SampleRateHz = 16000, ChannelConfig = 0, ChannelRoles = new List<string> { "microphone" } }
                            }
                        }
                    },
                    new UsbInterfaceSpec
                    {
                        InterfaceNumber = 3, Function = "hid",
                        AltSettings = new List<UsbAltSettingSpec>
                        {
                            new UsbAltSettingSpec
                            {
                                AltSetting = 0, InterfaceClass = 3, InterfaceSubClass = 0, InterfaceProtocol = 0,
                                Endpoints = new List<UsbEndpointSpec>
                                {
                                    new UsbEndpointSpec { Address = 132, TransferType = "interrupt", MaxPacketSize = 64, Interval = 5 },
                                    new UsbEndpointSpec { Address = 3, TransferType = "interrupt", MaxPacketSize = 64, Interval = 5 }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    // Nintendo Switch Pro Controller, presented as a genuine USB HID pad
    // (VID 0x057E / PID 0x2009) over the USB/IP backend so SDL3's
    // HIDAPI_DriverSwitch takes the USB init path (0x80 handshake, 0x01
    // subcommands) and eden sees a real Pro Controller with sensors and
    // HD rumble. The HID report descriptor is DJm00n/ControllersInfo's
    // USB dump (nativeDescriptor from GKMD's switch-pro profile);
    // it declares reports 0x30/0x21/0x81 input and 0x01/0x10/0x80/0x82
    // output, each 63-byte payload (64 including the report ID). The
    // 0x80/0x01/0x10 interception and SPI image live in
    // UsbipEmulatedDevice's Switch responder; the input body is packed by
    // Core.SwitchProInput.

    // The Nintendo Switch family shares one USB HID report descriptor on
    // USB: DJm00n/ControllersInfo's dump declares reports 0x30/0x21/0x81
    // input and 0x01/0x10/0x80/0x82 output, each a 63-byte payload (64
    // including the report ID). The OS HID stack only needs the top-level
    // Generic Desktop / Joystick collection; SDL and eden parse the wire
    // bytes by fixed offset, so the same descriptor serves Pro and Joy-Con.
    private const string SwitchUsbReportDescriptor =
        "050115000904a1018530050105091901290a150025017501950a5500650081020509190b290e" +
        "150025017501950481027501950281030b01000100a1000b300001000b310001000b32000100" +
        "0b35000100150027ffff0000751095048102c00b39000100150025073500463b0165147504" +
        "950181020509190f2912150025017501950481027508953481030600ff852109017508953f" +
        "8103858109027508953f8103850109037508953f9183851009047508953f91838580090575" +
        "08953f9183858209067508953f9183c0";

    // One HID interface (class 3); the HID class descriptor declares the
    // 0xCB = 203-byte report descriptor, interrupt IN 0x81 and interrupt
    // OUT 0x01, both 64-byte, interval 8.
    private const string SwitchUsbConfigurationDescriptor =
        "090229000101008032" +
        "090400000103000000" +
        "09211101000122CB00" +
        "07058103400008" +
        "07050103400008";

    private static ControllerProfile BuildSwitchPro()
    {
        const string nativeDescriptor = SwitchUsbReportDescriptor;

        var uc = new UsbConfigurationSpec
        {
            ConfigurationValue = 1,
            Attributes = 0x80,
            MaxPowerMilliamps = 100,
            BusSpeed = "full",
            // bcdUSB 2.00, class 0, bMaxPacketSize0 64, VID 057E PID 2009,
            // bcdDevice 0x0200, iMfr 1 / iProduct 2 / iSerial 3.
            DeviceDescriptorHex = "12010002000000407E050920000201020301",
            // One HID interface (class 3), HID class descriptor declares the
            // 0xCB = 203-byte report descriptor, interrupt IN 0x81 and
            // interrupt OUT 0x01, both 64-byte, interval 8.
            ConfigurationDescriptorHex = SwitchUsbConfigurationDescriptor,
            Interfaces = new List<UsbInterfaceSpec>
            {
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 0, Function = "hid",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 3, InterfaceSubClass = 0, InterfaceProtocol = 0,
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x81, TransferType = "interrupt", MaxPacketSize = 64, Interval = 8 },
                                new UsbEndpointSpec { Address = 0x01, TransferType = "interrupt", MaxPacketSize = 64, Interval = 8 }
                            }
                        }
                    }
                }
            }
        };

        return new ControllerProfile
        {
            Id = "switch-pro",
            Name = "Nintendo Switch Pro Controller (USB)",
            Vendor = "Nintendo",
            Vid = "0x057E",
            Pid = "0x2009",
            ProductString = "Pro Controller",
            ManufacturerString = "Nintendo Co., Ltd.",
            SerialNumberString = "000000000001",
            Type = "gamepad",
            Descriptor = nativeDescriptor,
            InputReportSize = 64,
            Notes = "Genuine USB Switch Pro persona. SDL3's HIDAPI_DriverSwitch " +
                    "selects the USB init path from the USB bus type (HIDAPI " +
                    "reports a USB device path), so the responder must answer " +
                    "0x80 Handshake/HighSpeed with 0x81 replies and the 0x01 " +
                    "subcommand stream (device info, input mode, SPI reads, IMU " +
                    "enable) with 0x21 replies. Input reports are the 48-byte " +
                    "SwitchControllerStatePacket_t + 3x SwitchControllerIMUState_t " +
                    "in report 0x30 (padded to the descriptor's 64 bytes). HD " +
                    "rumble arrives in output 0x01/0x10 as two 4-byte blocks; " +
                    "GamepadSession decodes them and synthesizes sine audio for " +
                    "the phone's two LRAs.",
            UsbConfiguration = uc
        };
    }

    // One Joy-Con half, presented as a genuine USB HID pad (VID 0x057E /
    // PID 0x2006 left, 0x2007 right) over the USB/IP backend. SDL3's
    // HIDAPI_DriverJoyCons claims both PIDs on the USB bus and, when both
    // halves are present, SDL_HINT_JOYSTICK_HIDAPI_COMBINE_JOY_CONS pairs
    // them into a single virtual pad; eden's own joycon driver also
    // enumerates 0x2006/0x2007 directly. The wire protocol (0x80
    // handshake, 0x01 subcommands, 0x21 replies, 0x30 full-mode stream,
    // SPI calibration) is the same as the Pro's and lives in
    // UsbipEmulatedDevice's Switch responder; the only per-half difference
    // is the reported device type (1 = L, 2 = R) in the Status reply, the
    // device-info reply and SPI 0x6012.
    private static ControllerProfile BuildJoycon(string id, string pid, string product, string serial)
    {
        // bcdUSB 2.00, class 0, bMaxPacketSize0 64, VID 057E, bcdDevice
        // 0x0200, iMfr 1 / iProduct 2 / iSerial 3.
        string deviceDescriptor = pid == "0x2006"
            ? "12010002000000407E050620000201020301"
            : "12010002000000407E050720000201020301";

        var uc = new UsbConfigurationSpec
        {
            ConfigurationValue = 1,
            Attributes = 0x80,
            MaxPowerMilliamps = 100,
            BusSpeed = "full",
            DeviceDescriptorHex = deviceDescriptor,
            ConfigurationDescriptorHex = SwitchUsbConfigurationDescriptor,
            Interfaces = new List<UsbInterfaceSpec>
            {
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 0, Function = "hid",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 3, InterfaceSubClass = 0, InterfaceProtocol = 0,
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x81, TransferType = "interrupt", MaxPacketSize = 64, Interval = 8 },
                                new UsbEndpointSpec { Address = 0x01, TransferType = "interrupt", MaxPacketSize = 64, Interval = 8 }
                            }
                        }
                    }
                }
            }
        };

        return new ControllerProfile
        {
            Id = id,
            Name = $"Nintendo Switch {product} (USB)",
            Vendor = "Nintendo",
            Vid = "0x057E",
            Pid = pid,
            ProductString = product,
            ManufacturerString = "Nintendo Co., Ltd.",
            SerialNumberString = serial,
            Type = "gamepad",
            Descriptor = SwitchUsbReportDescriptor,
            InputReportSize = 64,
            Notes = "Genuine USB Joy-Con persona. SDL3's HIDAPI_DriverJoyCons " +
                    "claims 0x057E:0x2006 (L) and 0x057E:0x2007 (R) on the USB " +
                    "bus; with both halves attached the combined driver pairs " +
                    "them into one pad and forwards sensors/rumble to each. " +
                    "eden's own joycon driver also enumerates these PIDs and " +
                    "reads the device type from subcommand 0x02 and SPI " +
                    "0x6012. Input reports are the 48-byte SwitchController" +
                    "StatePacket_t + 3x IMU in report 0x30 (padded to 64); HD " +
                    "rumble arrives in output 0x01/0x10 and GamepadSession " +
                    "decodes it for the phone's LRAs.",
            UsbConfiguration = uc
        };
    }

    // ── HID keyboard + mouse personas ────────────────────────────────────
    //
    // GKME previously synthesized keyboard/mouse input with user-mode
    // SendInput (InputDll.dll). Those personas present the input as two
    // genuine USB HID devices on the USB/IP backend instead, exactly like
    // the gamepads, so the host sees real hardware and no injection DLL is
    // needed. Both descriptors are ported from VIIPER's device/keyboard and
    // device/mouse Go definitions (the keyboard uses the full 256-bit
    // key-bitmap report so any chord of keys survives) and validated against
    // GKMD's own report-descriptor parser at create time.

    // Keyboard report descriptor (68 bytes): 8-bit modifier field, 8-bit
    // reserved, 256-bit key bitmap, then the 5-bit LED output + 3-bit pad.
    private const string KeyboardUsbReportDescriptor =
        "05010906A101050719E029E7150025017501950881027508950181010507" +
        "190029FF150025017501960001810205081901290515002501750195059102" +
        "750395019101C0";

    // HID interface (class 3, no boot subclass — the report is not the 8-byte
    // boot layout), one interrupt IN endpoint. wDescriptorLength 0x44 = 68.
    private const string KeyboardUsbConfigurationDescriptor =
        "090222000101008032" +
        "090400000103000000" +
        "092111010001224400" +
        "0705810340000A";

    // Mouse report descriptor (119 bytes): 8 buttons, 16-bit relative X/Y
    // (the phone can hand us deltas up to ±32767 in one frame), and 16-bit
    // vertical Wheel / consumer AC Pan. Each scroll axis lives in its own
    // Logical collection alongside a 120-count Resolution Multiplier feature
    // (HID "Enhanced Wheel Support"): Windows maps 120 raw units to one
    // WHEEL_DELTA detent, which is exactly the unit the phone sends, so
    // sub-detent motion survives as true high-resolution scrolling. Layout
    // ported from hid-remapper's proven kb_mouse descriptor.
    private const string MouseUsbReportDescriptor =
        "05010902A1010901A1000509190129089508750125018102" +
        "0501093009319502751016008026FF7F8106" +
        "A1020948950175021500250135014578B10209383500450016008026FF7F75108106C0" +
        "A102094875021500250135014578B102350045007504B103050C16008026FF7F75100A38028106C0" +
        "C0C0";

    // HID interface (class 3), one interrupt IN endpoint (9-byte report → 16B
    // packet, bInterval 1 = 1 ms poll → up to 1 kHz). wDescriptorLength
    // 0x77 = 119.
    private const string MouseUsbConfigurationDescriptor =
        "090222000101008032" +
        "090400000103000000" +
        "092111010001227700" +
        "07058103100001";

    private static ControllerProfile BuildKeyboard()
    {
        var uc = new UsbConfigurationSpec
        {
            ConfigurationValue = 1,
            Attributes = 0x80,          // bus powered, no remote wakeup
            MaxPowerMilliamps = 100,
            BusSpeed = "full",
            // bcdUSB 2.00, class 0, bMaxPacketSize0 64, VID 2E8A PID 0010
            // (same VID as VIIPER's reference keyboard), bcdDevice 0x0100.
            DeviceDescriptorHex = "12010002000000408A2E1000000101020301",
            ConfigurationDescriptorHex = KeyboardUsbConfigurationDescriptor,
            InputReportSize = 34,
            Interfaces = new List<UsbInterfaceSpec>
            {
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 0, Function = "hid",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 3, InterfaceSubClass = 0, InterfaceProtocol = 0,
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x81, TransferType = "interrupt", MaxPacketSize = 64, Interval = 10 }
                            }
                        }
                    }
                }
            }
        };

        return new ControllerProfile
        {
            Id = "keyboard",
            Name = "HID Keyboard (USB)",
            Vendor = "GKME",
            Vid = "0x2E8A",
            Pid = "0x0010",
            ProductString = "HID Keyboard",
            ManufacturerString = "GKME",
            SerialNumberString = "1337",
            Type = "keyboard",
            Descriptor = KeyboardUsbReportDescriptor,
            InputReportSize = 34,
            Notes = "Full N-key-rollover USB HID keyboard on the USB/IP backend. " +
                    "Input is delivered verbatim from the shared-memory DATA section " +
                    "as the 34-byte report [modifiers][reserved][256-bit key bitmap].",
            UsbConfiguration = uc
        };
    }

    private static ControllerProfile BuildMouse()
    {
        var uc = new UsbConfigurationSpec
        {
            ConfigurationValue = 1,
            Attributes = 0x80,          // bus powered, no remote wakeup
            MaxPowerMilliamps = 100,
            BusSpeed = "full",
            // bcdUSB 2.00, class 0, bMaxPacketSize0 64, VID 2E8A PID 0011,
            // bcdDevice 0x0100.
            DeviceDescriptorHex = "12010002000000408A2E1100000101020301",
            ConfigurationDescriptorHex = MouseUsbConfigurationDescriptor,
            InputReportSize = 9,
            ResolutionMultiplier = 120,
            Interfaces = new List<UsbInterfaceSpec>
            {
                new UsbInterfaceSpec
                {
                    InterfaceNumber = 0, Function = "hid",
                    AltSettings = new List<UsbAltSettingSpec>
                    {
                        new UsbAltSettingSpec
                        {
                            AltSetting = 0, InterfaceClass = 3, InterfaceSubClass = 0, InterfaceProtocol = 0,
                            Endpoints = new List<UsbEndpointSpec>
                            {
                                new UsbEndpointSpec { Address = 0x81, TransferType = "interrupt", MaxPacketSize = 16, Interval = 1 }
                            }
                        }
                    }
                }
            }
        };

        return new ControllerProfile
        {
            Id = "mouse",
            Name = "HID Mouse (USB)",
            Vendor = "GKME",
            Vid = "0x2E8A",
            Pid = "0x0011",
            ProductString = "HID Mouse",
            ManufacturerString = "GKME",
            SerialNumberString = "1337",
            Type = "mouse",
            Descriptor = MouseUsbReportDescriptor,
            InputReportSize = 9,
            Notes = "8-button USB HID mouse with 16-bit relative X/Y and high-" +
                    "resolution 16-bit vertical wheel / horizontal pan on the " +
                    "USB/IP backend. A 120-count Resolution Multiplier feature makes " +
                    "the host map 120 raw units to one detent, so input is delivered " +
                    "verbatim as the 9-byte report [buttons][dxLE16][dyLE16][wheelLE16]" +
                    "[panLE16] in WHEEL_DELTA units.",
            UsbConfiguration = uc
        };
    }
}
