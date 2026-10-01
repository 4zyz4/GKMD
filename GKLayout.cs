using System;
using System.Collections.Generic;

namespace GKMD;

// =====================================================================
// GKLayout — structured per-profile physical-design declaration (v1.3.9)
//
// Discriminated by GKLayoutKind. Each per-kind layout captures the
// device's published manufacturer-described shape: which descriptor
// fields are sticks vs wheels vs pedals vs throttles, how sub-modules
// (a HOTAS throttle quadrant, a wheel's pedal cluster, a HOTAS rudder
// module) cluster, what semantic role each axis and button serves.
//
// Two surfaces:
//   • GKProfile.Layout — the full structured layout (rich, when authored)
//   • GKProfile.StickCount / TriggerCount / Sticks / Triggers — simple
//     derived views computed FROM Layout when authored, from the
//     classifier heuristic when not (backward-compatible fallback)
//
// Profiles without a layout block keep the v1.3.8 classifier-derived
// behavior unchanged.
// =====================================================================

/// <summary>Discriminator for <see cref="GKLayout"/>. Each value selects
/// a per-kind concrete record with the fields that device class
/// requires.</summary>
public enum GKLayoutKind
{
    /// <summary>No layout authored. Consumers fall back to classifier-
    /// derived StickCount/TriggerCount/AvailableAxes.</summary>
    Unspecified = 0,
    Gamepad,
    Joystick,
    FlightStick,
    Hotas,
    Wheel,
    Pedals,
    Shifter,
    Handbrake,
    SingleAxisAccessory,
    ArcadeStick,
    DancePad,
    Guitar,
    MotionWand,
    Remote,
    ControllerAdapter,
}

/// <summary>Role an analog axis serves on the physical device. Used by
/// every layout kind to label the descriptor's input fields with their
/// real-world meaning so consumers can render the right widget.</summary>
public enum GKAxisRole
{
    Unknown = 0,
    LeftStickX,    LeftStickY,
    RightStickX,   RightStickY,
    LeftTrigger,   RightTrigger,
    Wheel,                    // steering wheel rotation
    Throttle,                 // flight throttle / racing throttle pedal
    Brake,                    // racing brake pedal / flight toe brake
    Clutch,                   // racing clutch pedal
    Accelerator,              // racing accelerator pedal (alias for Throttle on some)
    Rudder,                   // separate rudder pedal axis
    Aileron,                  // flight aileron
    Elevator,                 // flight elevator
    TwistRudder,              // stick-grip rudder via twist
    ThrottleSlider,           // slider on a flight stick base used as throttle
    Dial,                     // generic rotary dial
    ScrollWheel,              // throttle module scroll wheel (X52)
    ModeDial,                 // multi-position rotary mode selector
    Friction,                 // throttle friction adjuster (HOTAS)
    MiniStickX, MiniStickY,   // throttle module mini-stick (X52)
    HandbrakeAxis,            // single-axis handbrake lever
}

/// <summary>Role a button serves on the physical device.</summary>
public enum GKButtonRole
{
    Unknown = 0,

    // Standard gamepad face
    FaceA, FaceB, FaceX, FaceY,
    FaceCross, FaceCircle, FaceSquare, FaceTriangle,

    // Shoulders + stick clicks
    LeftBumper, RightBumper,
    LeftTriggerClick, RightTriggerClick,
    LeftStickClick, RightStickClick,

    // System
    Back, View, Share, Start, Options, Menu, Guide, Home, Capture, Ps, Xbox,
    Mute, ProfileSwitch,

    // Vendor button with no cross-vendor role. Currently the Switch 2
    // family's C button, which opens GameChat on real hardware.
    Misc1,

    // Elite paddles + extra remappable
    PaddleP1, PaddleP2, PaddleP3, PaddleP4,
    PaddleM1, PaddleM2, PaddleM3, PaddleM4, PaddleM5, PaddleM6,

    // Wheel paddle shifters
    PaddleShifterUp, PaddleShifterDown,

    // Flight stick / HOTAS
    Trigger, Pinkie, Thumb,
    ModeSwitchLow, ModeSwitchMid, ModeSwitchHigh,

    // H-pattern shifter gears
    Gear1, Gear2, Gear3, Gear4, Gear5, Gear6, Gear7, GearReverse,

    // Sequential shifter
    ShifterUp, ShifterDown,

    // Guitar
    StrumUp, StrumDown,
    FretGreen, FretRed, FretYellow, FretBlue, FretOrange,
    SoloGreen, SoloRed, SoloYellow, SoloBlue, SoloOrange,
    Whammy, // (axis, here for completeness if some descriptor declares as button)

    // PS Move
    Move, T,

    // D-pad as buttons (when descriptor encodes dpad as 4 separate buttons)
    DpadUp, DpadDown, DpadLeft, DpadRight,
}

public enum GKPedalType        { Unknown, Potentiometer, HallEffect, LoadCell, Magnetic }
public enum GKRudderKind       { Twist, Rocker, Pedals }
public enum GKShifterKind      { HPattern, Sequential, PaddleLeft, PaddleRight }
public enum GKShifterActuation { Digital, AnalogSqueeze }
public enum GKHatLocation      { Unknown, StickTop, StickThumb, Pinkie, Base, ThrottleModule, WheelHub }
public enum GKTriggerKind      { Analog, Digital }
public enum GKDpadEncoding     { Hat, Buttons }
public enum GKStickSide        { Left, Right }
public enum GKTriggerSide      { Left, Right }
public enum GKRumbleKind       { None, SingleErm, DualErm, VoiceCoilHaptic, ImpulseTriggers }

// =====================================================================
// Reusable sub-records
// =====================================================================

public sealed record GKStick
{
    public GKStickSide Side { get; init; }
    public GKAxis XAxis { get; init; }
    public GKAxis YAxis { get; init; }
    public int? ClickButton { get; init; }
}

public sealed record GKTrigger
{
    public GKAxis Axis { get; init; }
    public GKTriggerSide Side { get; init; }
    public GKTriggerKind Kind { get; init; } = GKTriggerKind.Analog;
    public int? Stages { get; init; } // 1 = single-stage, 2 = two-stage detent (X52 trigger)
}

public sealed record GKDpad
{
    public GKDpadEncoding Encoding { get; init; }
    public GKAxis? HatAxis { get; init; }
    public int? HatPositions { get; init; }
    public int? UpButton { get; init; }
    public int? DownButton { get; init; }
    public int? LeftButton { get; init; }
    public int? RightButton { get; init; }
}

public sealed record GKButtonBinding
{
    public GKButtonRole Role { get; init; }
    public int ButtonIndex { get; init; }
    public string? Label { get; init; } // optional human-friendly override
}

public sealed record GKHatBinding
{
    public GKAxis Axis { get; init; }
    public int Positions { get; init; }
    public GKHatLocation Location { get; init; } = GKHatLocation.Unknown;
    public string? Role { get; init; } // free-text role for HOTAS hats with non-enum roles
}

public sealed record GKHaptics
{
    public GKRumbleKind Rumble { get; init; } = GKRumbleKind.None;
    public bool TriggerHaptics { get; init; }
}

public sealed record GKImu
{
    public bool Accelerometer { get; init; }
    public bool Gyroscope { get; init; }
    public bool Magnetometer { get; init; }
}

public sealed record GKRudder
{
    public GKAxis Axis { get; init; }
    public GKRudderKind Kind { get; init; }
}

public sealed record GKWheelSpec
{
    public GKAxis Axis { get; init; }
    public int? RotationDegrees { get; init; }
    public bool ForceFeedback { get; init; }
}

public sealed record GKPedal
{
    public GKAxis Axis { get; init; }
    public GKAxisRole Role { get; init; }   // Throttle | Brake | Clutch | Rudder | Accelerator
    public GKPedalType Type { get; init; } = GKPedalType.Unknown;
}

public sealed record GKShifter
{
    public GKShifterKind Kind { get; init; }
    public int? ButtonIndex { get; init; }
    public GKShifterActuation Actuation { get; init; } = GKShifterActuation.Digital;
}

public sealed record GKRotaryEncoder
{
    public GKAxis Axis { get; init; }
    public GKAxisRole Role { get; init; } = GKAxisRole.Dial;
    public int? Positions { get; init; } // for detented encoders; null = continuous
}

public sealed record GKRevIndicator
{
    public int LedCount { get; init; }
}

public sealed record GKTriggerButton
{
    public int ButtonIndex { get; init; }
    public int Stages { get; init; } = 1;
}

// =====================================================================
// Simple-view records (the "I just want sticks and triggers" surface)
// =====================================================================

/// <summary>v1.3.9 — flat-list view of one stick a profile exposes,
/// surfaced via <see cref="GKProfile.Sticks"/>. Variable count: typical
/// gamepad has 2 (left + right), a flight stick / wheel / HOTAS has 1,
/// a pedals-only device has 0. PadForge-style consumers iterate this
/// list to render a stick widget per entry.</summary>
public sealed record GKSimpleStick
{
    public GKAxis XAxis { get; init; }
    public GKAxis YAxis { get; init; }   // GKAxis.None when only X is used (1D stick)
    public GKAxisRole RoleX { get; init; } = GKAxisRole.Unknown;
    public GKAxisRole RoleY { get; init; } = GKAxisRole.Unknown;
    public string? Label { get; init; }
}

/// <summary>v1.3.9 — flat-list view of one trigger axis the profile
/// exposes, surfaced via <see cref="GKProfile.Triggers"/>. Variable count:
/// typical gamepad has 2 (LT/RT), a 3-pedal sim set has 3 (gas/brake/clutch),
/// a handbrake has 1, a HOTAS may have throttle + twist rudder + slider.
/// The first two entries are also reachable via the encoder's
/// <see cref="GKGamepadState.LeftTrigger"/> / <see cref="GKGamepadState.RightTrigger"/>
/// slots; additional triggers are encoder-reachable only via
/// <see cref="GKGamepadState.ExtraAxes"/>.</summary>
public sealed record GKSimpleTrigger
{
    public GKAxis Axis { get; init; }
    public GKAxisRole Role { get; init; } = GKAxisRole.Unknown;
    public string? Label { get; init; }
}

// =====================================================================
// GKLayout — abstract base + per-kind concrete records
// =====================================================================

/// <summary>Per-profile structured physical-design declaration. Each
/// concrete subclass corresponds to one <see cref="GKLayoutKind"/>
/// value and exposes the fields that kind requires.</summary>
public abstract record GKLayout
{
    public abstract GKLayoutKind Kind { get; }

    /// <summary>Optional manufacturer/spec source URL the layout was
    /// authored from. Stripped from embedded resource at build time;
    /// useful during development/audit.</summary>
    public string? Source { get; init; }
}

public sealed record GKUnspecifiedLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Unspecified;
    public string? Note { get; init; }
}

public sealed record GKGamepadLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Gamepad;
    public IReadOnlyList<GKStick> Sticks { get; init; } = Array.Empty<GKStick>();
    public IReadOnlyList<GKTrigger> Triggers { get; init; } = Array.Empty<GKTrigger>();
    public GKDpad? Dpad { get; init; }
    public IReadOnlyList<GKButtonBinding> FaceButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> ShoulderButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> SystemButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> ExtraButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public GKHaptics? Haptics { get; init; }
    public GKImu? Imu { get; init; }
}

public sealed record GKJoystickLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Joystick;
    public GKStick Stick { get; init; } = new();
    public GKRudder? Rudder { get; init; }
    public GKPedal? Throttle { get; init; }   // single-throttle slider on stick base
    public IReadOnlyList<GKHatBinding> Hats { get; init; } = Array.Empty<GKHatBinding>();
    public GKTriggerButton? Trigger { get; init; }
    public IReadOnlyList<GKButtonBinding> StickButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> BaseButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public bool ForceFeedback { get; init; }
}

public sealed record GKFlightStickLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.FlightStick;
    public GKStick Stick { get; init; } = new();
    public GKRudder? Rudder { get; init; }
    public GKPedal? Throttle { get; init; }
    public IReadOnlyList<GKHatBinding> Hats { get; init; } = Array.Empty<GKHatBinding>();
    public GKTriggerButton? Trigger { get; init; }
    public IReadOnlyList<GKButtonBinding> StickButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> BaseButtons { get; init; } = Array.Empty<GKButtonBinding>();
}

public sealed record GKHotasLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Hotas;
    public GKStick Stick { get; init; } = new();
    public GKRudder? StickRudder { get; init; }       // twist-grip rudder on the stick (if any)
    public IReadOnlyList<GKHatBinding> StickHats { get; init; } = Array.Empty<GKHatBinding>();
    public GKTriggerButton? StickTrigger { get; init; }
    public IReadOnlyList<GKButtonBinding> StickButtons { get; init; } = Array.Empty<GKButtonBinding>();

    // Throttle module
    public GKPedal? ThrottlePrimary { get; init; }       // main throttle (Throttle role)
    public IReadOnlyList<GKRotaryEncoder> ThrottleSecondary { get; init; } = Array.Empty<GKRotaryEncoder>();
    public IReadOnlyList<GKHatBinding> ThrottleHats { get; init; } = Array.Empty<GKHatBinding>();
    public IReadOnlyList<GKButtonBinding> ThrottleButtons { get; init; } = Array.Empty<GKButtonBinding>();

    // Rudder module (some HOTAS sets bundle a separate rudder rocker on the throttle base)
    public GKPedal? RudderModule { get; init; }
}

public sealed record GKWheelLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Wheel;
    public GKWheelSpec Wheel { get; init; } = new();
    public IReadOnlyList<GKPedal> Pedals { get; init; } = Array.Empty<GKPedal>();
    public IReadOnlyList<GKShifter> Shifters { get; init; } = Array.Empty<GKShifter>();
    public IReadOnlyList<GKButtonBinding> WheelButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKRotaryEncoder> RotaryEncoders { get; init; } = Array.Empty<GKRotaryEncoder>();
    public GKDpad? Dpad { get; init; }
    public GKRevIndicator? RevIndicator { get; init; }
}

public sealed record GKPedalsLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Pedals;
    public IReadOnlyList<GKPedal> Pedals { get; init; } = Array.Empty<GKPedal>();
}

public sealed record GKShifterLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Shifter;
    public IReadOnlyList<GKShifterKind> Modes { get; init; } = Array.Empty<GKShifterKind>();
    public IReadOnlyDictionary<string, GKButtonBinding>? HPatternGears { get; init; }
    public int? SequentialUpButton { get; init; }
    public int? SequentialDownButton { get; init; }
}

public sealed record GKHandbrakeLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Handbrake;
    public GKAxis Axis { get; init; }
}

public sealed record GKSingleAxisAccessoryLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.SingleAxisAccessory;
    public GKAxis Axis { get; init; }
    public GKAxisRole Role { get; init; } = GKAxisRole.Unknown;
}

public sealed record GKArcadeStickLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.ArcadeStick;
    public GKDpad Joystick { get; init; } = new();
    public IReadOnlyList<GKButtonBinding> FaceButtons { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> SystemButtons { get; init; } = Array.Empty<GKButtonBinding>();
}

public sealed record GKDancePadLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.DancePad;
    public IReadOnlyDictionary<string, GKButtonBinding> Pads { get; init; } =
        new Dictionary<string, GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> SystemButtons { get; init; } = Array.Empty<GKButtonBinding>();
}

public sealed record GKGuitarLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Guitar;
    public IReadOnlyList<GKButtonBinding> Frets { get; init; } = Array.Empty<GKButtonBinding>();
    public IReadOnlyList<GKButtonBinding> SoloFrets { get; init; } = Array.Empty<GKButtonBinding>();
    public GKDpad? Strum { get; init; }    // up/down dpad encoding
    public GKAxis? WhammyAxis { get; init; }
    public int? TiltButton { get; init; }
    public IReadOnlyList<GKButtonBinding> SystemButtons { get; init; } = Array.Empty<GKButtonBinding>();
}

public sealed record GKMotionWandLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.MotionWand;
    public GKTriggerButton? Trigger { get; init; }
    public GKAxis? TriggerAxis { get; init; } // PS Move T trigger has analog axis
    public IReadOnlyList<GKButtonBinding> Buttons { get; init; } = Array.Empty<GKButtonBinding>();
    public GKImu? Imu { get; init; }
    public bool RgbLightbar { get; init; }
}

public sealed record GKRemoteLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.Remote;
    public IReadOnlyList<GKButtonBinding> Buttons { get; init; } = Array.Empty<GKButtonBinding>();
}

public sealed record GKControllerAdapterLayout : GKLayout
{
    public override GKLayoutKind Kind => GKLayoutKind.ControllerAdapter;
    public int Ports { get; init; }
    public string? PerPortLayout { get; init; } // free-text description of each port's per-port shape
}
