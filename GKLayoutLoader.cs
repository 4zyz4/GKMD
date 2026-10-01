using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GKMD.Internal;

/// <summary>Polymorphic JSON converter for <see cref="GKLayout"/>. Reads
/// the discriminator <c>kind</c> property and materializes the matching
/// concrete record. snake_case strings in JSON are converted to enum
/// values (matching <see cref="JsonNamingPolicy.SnakeCaseLower"/>).</summary>
internal sealed class GKLayoutJsonConverter : JsonConverter<GKLayout>
{
    public override GKLayout? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kindElem))
            throw new JsonException("GKLayout: missing required 'kind' property");
        var kindStr = kindElem.GetString();
        if (string.IsNullOrEmpty(kindStr))
            throw new JsonException("GKLayout: 'kind' is empty");

        var kind = SnakeCaseToEnum<GKLayoutKind>(kindStr)
                   ?? throw new JsonException($"GKLayout: unknown kind '{kindStr}'");

        var json = root.GetRawText();
        var sub = options;  // reuse options (already configured with snake_case enum support)
#pragma warning disable IL3050
        return kind switch
        {
            GKLayoutKind.Unspecified         => JsonSerializer.Deserialize<GKUnspecifiedLayout>(json, sub),
            GKLayoutKind.Gamepad             => JsonSerializer.Deserialize<GKGamepadLayout>(json, sub),
            GKLayoutKind.Joystick            => JsonSerializer.Deserialize<GKJoystickLayout>(json, sub),
            GKLayoutKind.FlightStick         => JsonSerializer.Deserialize<GKFlightStickLayout>(json, sub),
            GKLayoutKind.Hotas               => JsonSerializer.Deserialize<GKHotasLayout>(json, sub),
            GKLayoutKind.Wheel               => JsonSerializer.Deserialize<GKWheelLayout>(json, sub),
            GKLayoutKind.Pedals              => JsonSerializer.Deserialize<GKPedalsLayout>(json, sub),
            GKLayoutKind.Shifter             => JsonSerializer.Deserialize<GKShifterLayout>(json, sub),
            GKLayoutKind.Handbrake           => JsonSerializer.Deserialize<GKHandbrakeLayout>(json, sub),
            GKLayoutKind.SingleAxisAccessory => JsonSerializer.Deserialize<GKSingleAxisAccessoryLayout>(json, sub),
            GKLayoutKind.ArcadeStick         => JsonSerializer.Deserialize<GKArcadeStickLayout>(json, sub),
            GKLayoutKind.DancePad            => JsonSerializer.Deserialize<GKDancePadLayout>(json, sub),
            GKLayoutKind.Guitar              => JsonSerializer.Deserialize<GKGuitarLayout>(json, sub),
            GKLayoutKind.MotionWand          => JsonSerializer.Deserialize<GKMotionWandLayout>(json, sub),
            GKLayoutKind.Remote              => JsonSerializer.Deserialize<GKRemoteLayout>(json, sub),
            GKLayoutKind.ControllerAdapter   => JsonSerializer.Deserialize<GKControllerAdapterLayout>(json, sub),
            _                                => throw new JsonException($"GKLayout: unhandled kind {kind}")
        };
#pragma warning restore IL3050
    }

    public override void Write(Utf8JsonWriter writer, GKLayout value, JsonSerializerOptions options)
    {
#pragma warning disable IL2026
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
#pragma warning restore IL2026
    }

    /// <summary>Convert a snake_case JSON string ("left_stick_x") to its
    /// matching PascalCase enum value (GKAxisRole.LeftStickX). Returns null
    /// when no match. Used by the converter and by the per-enum string
    /// readers below.</summary>
    internal static T? SnakeCaseToEnum<T>(string s) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(s)) return null;
        var pascal = string.Concat(
            s.Split('_').Select(p =>
                p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        return Enum.TryParse<T>(pascal, ignoreCase: true, out var v) ? v : null;
    }
}

/// <summary>JSON serializer options preconfigured for GKLayout
/// deserialization. Registered once per ControllerProfile load.</summary>
public static class GKLayoutJsonOptions
{
    public static readonly JsonSerializerOptions Default = Build();

    private static JsonSerializerOptions Build()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,    // matches legacy ControllerProfile load behavior
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        // GKAxisJsonConverter must come BEFORE JsonStringEnumConverter or
        // the latter wins for GKAxis (it's an enum) and produces snake_case
        // names like "x" / "left_stick_x" instead of the canonical PascalCase
        // axis names ("X", "Y", "LeftStickX") that the descriptor parser
        // emits.
        opts.Converters.Add(new GKAxisJsonConverter());
#pragma warning disable IL3050
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
#pragma warning restore IL3050
        opts.Converters.Add(new GKLayoutJsonConverter());
        return opts;
    }
}

/// <summary>Reads <see cref="GKAxis"/> from JSON as the canonical
/// short string ("X", "Y", "Slider", "Throttle", etc. — matches the enum
/// PascalCase names). The GKAxis enum is page-and-usage encoded under the
/// hood, but in JSON we want human-readable names.</summary>
internal sealed class GKAxisJsonConverter : JsonConverter<GKAxis>
{
    public override GKAxis Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return GKAxis.None;
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s)) return GKAxis.None;
        if (Enum.TryParse<GKAxis>(s, ignoreCase: true, out var v))
            return v;
        throw new JsonException($"GKAxis: unknown axis name '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, GKAxis value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>Validates an authored <see cref="GKLayout"/> against the
/// profile's HID descriptor. Every <c>GKAxis</c> reference must resolve
/// to a real declared analog input field; every button index must be
/// within <c>[0, ButtonCount)</c>.
///
/// Throws <see cref="GKLayoutValidationException"/> with a structured path
/// into the layout for any mismatch.</summary>
public static class GKLayoutValidator
{
    public static void Validate(GKLayout layout, HidReportBuilder layoutDescriptor)
    {
        var declaredAxes = new HashSet<GKAxis>(layoutDescriptor.AxisFields.Keys);
        // Hat Switch is its own input shape (not in AxisFields by design)
        // but layouts reference it via GKAxis.Hat. Include it in the
        // validator's declared set when the descriptor declares a hat.
        if (layoutDescriptor.HatSwitch != null)
            declaredAxes.Add(GKAxis.Hat);
        int buttonCount = layoutDescriptor.Buttons.Count;
        var ctx = new ValidationContext(declaredAxes, buttonCount, layout.Kind);
        WalkLayout(layout, ctx);
    }

    private sealed record ValidationContext(HashSet<GKAxis> DeclaredAxes, int ButtonCount, GKLayoutKind Kind);

    private static void WalkLayout(GKLayout layout, ValidationContext ctx)
    {
        switch (layout)
        {
            case GKUnspecifiedLayout: return;

            case GKGamepadLayout g:
                foreach (var s in g.Sticks)         CheckAxis(ctx, s.XAxis, "sticks[].x"); CheckAxis(ctx, GetY(g.Sticks), "sticks[].y");
                foreach (var t in g.Triggers)       CheckAxis(ctx, t.Axis, "triggers[].axis");
                foreach (var s in g.Sticks) if (s.ClickButton is int cb) CheckButton(ctx, cb, "sticks[].click_button");
                CheckDpad(ctx, g.Dpad, "dpad");
                CheckBindings(ctx, g.FaceButtons,     "face_buttons");
                CheckBindings(ctx, g.ShoulderButtons, "shoulder_buttons");
                CheckBindings(ctx, g.SystemButtons,   "system_buttons");
                CheckBindings(ctx, g.ExtraButtons,    "extra_buttons");
                break;

            case GKJoystickLayout j:
                CheckAxis(ctx, j.Stick.XAxis, "stick.x");
                CheckAxis(ctx, j.Stick.YAxis, "stick.y");
                if (j.Stick.ClickButton is int jcb) CheckButton(ctx, jcb, "stick.click_button");
                if (j.Rudder is { } jr)   CheckAxis(ctx, jr.Axis, "rudder.axis");
                if (j.Throttle is { } jt) CheckAxis(ctx, jt.Axis, "throttle.axis");
                CheckHats(ctx, j.Hats, "hats");
                CheckTriggerButton(ctx, j.Trigger, "trigger");
                CheckBindings(ctx, j.StickButtons, "stick_buttons");
                CheckBindings(ctx, j.BaseButtons,  "base_buttons");
                break;

            case GKFlightStickLayout f:
                CheckAxis(ctx, f.Stick.XAxis, "stick.x");
                CheckAxis(ctx, f.Stick.YAxis, "stick.y");
                if (f.Rudder is { } fr)   CheckAxis(ctx, fr.Axis, "rudder.axis");
                if (f.Throttle is { } ft) CheckAxis(ctx, ft.Axis, "throttle.axis");
                CheckHats(ctx, f.Hats, "hats");
                CheckTriggerButton(ctx, f.Trigger, "trigger");
                CheckBindings(ctx, f.StickButtons, "stick_buttons");
                CheckBindings(ctx, f.BaseButtons,  "base_buttons");
                break;

            case GKHotasLayout h:
                CheckAxis(ctx, h.Stick.XAxis, "stick.x");
                CheckAxis(ctx, h.Stick.YAxis, "stick.y");
                if (h.StickRudder is { } sr) CheckAxis(ctx, sr.Axis, "stick_rudder.axis");
                CheckHats(ctx, h.StickHats, "stick_hats");
                CheckTriggerButton(ctx, h.StickTrigger, "stick_trigger");
                CheckBindings(ctx, h.StickButtons, "stick_buttons");
                if (h.ThrottlePrimary is { } tp) CheckAxis(ctx, tp.Axis, "throttle_primary.axis");
                foreach (var enc in h.ThrottleSecondary) CheckAxis(ctx, enc.Axis, "throttle_secondary[].axis");
                CheckHats(ctx, h.ThrottleHats, "throttle_hats");
                CheckBindings(ctx, h.ThrottleButtons, "throttle_buttons");
                if (h.RudderModule is { } rm) CheckAxis(ctx, rm.Axis, "rudder_module.axis");
                break;

            case GKWheelLayout w:
                CheckAxis(ctx, w.Wheel.Axis, "wheel.axis");
                foreach (var p in w.Pedals)            CheckAxis(ctx, p.Axis, "pedals[].axis");
                foreach (var s in w.Shifters) if (s.ButtonIndex is int sb) CheckButton(ctx, sb, "shifters[].button_index");
                foreach (var enc in w.RotaryEncoders)  CheckAxis(ctx, enc.Axis, "rotary_encoders[].axis");
                CheckBindings(ctx, w.WheelButtons, "wheel_buttons");
                CheckDpad(ctx, w.Dpad, "dpad");
                break;

            case GKPedalsLayout pl:
                foreach (var p in pl.Pedals) CheckAxis(ctx, p.Axis, "pedals[].axis");
                break;

            case GKShifterLayout sh:
                if (sh.SequentialUpButton   is int sub) CheckButton(ctx, sub, "sequential_up_button");
                if (sh.SequentialDownButton is int sdb) CheckButton(ctx, sdb, "sequential_down_button");
                if (sh.HPatternGears != null)
                    foreach (var (key, b) in sh.HPatternGears)
                        CheckButton(ctx, b.ButtonIndex, $"h_pattern_gears[{key}].button_index");
                break;

            case GKHandbrakeLayout hb:
                CheckAxis(ctx, hb.Axis, "axis");
                break;

            case GKSingleAxisAccessoryLayout sa:
                CheckAxis(ctx, sa.Axis, "axis");
                break;

            case GKArcadeStickLayout a:
                CheckDpad(ctx, a.Joystick, "joystick");
                CheckBindings(ctx, a.FaceButtons,   "face_buttons");
                CheckBindings(ctx, a.SystemButtons, "system_buttons");
                break;

            case GKDancePadLayout d:
                if (d.Pads != null)
                    foreach (var (key, b) in d.Pads)
                        CheckButton(ctx, b.ButtonIndex, $"pads[{key}].button_index");
                CheckBindings(ctx, d.SystemButtons, "system_buttons");
                break;

            case GKGuitarLayout gt:
                CheckBindings(ctx, gt.Frets,         "frets");
                CheckBindings(ctx, gt.SoloFrets,     "solo_frets");
                CheckBindings(ctx, gt.SystemButtons, "system_buttons");
                CheckDpad(ctx, gt.Strum, "strum");
                if (gt.WhammyAxis is GKAxis whammy && whammy != GKAxis.None)
                    CheckAxis(ctx, whammy, "whammy_axis");
                if (gt.TiltButton is int tb) CheckButton(ctx, tb, "tilt_button");
                break;

            case GKMotionWandLayout mw:
                if (mw.TriggerAxis is GKAxis ta && ta != GKAxis.None)
                    CheckAxis(ctx, ta, "trigger_axis");
                CheckTriggerButton(ctx, mw.Trigger, "trigger");
                CheckBindings(ctx, mw.Buttons, "buttons");
                break;

            case GKRemoteLayout r:
                CheckBindings(ctx, r.Buttons, "buttons");
                break;

            case GKControllerAdapterLayout:
                // adapter ports may have varying per-port descriptors;
                // free-text per_port_layout is not validated here
                break;
        }
    }

    private static GKAxis GetY(IEnumerable<GKStick> sticks)
        => sticks.FirstOrDefault()?.YAxis ?? GKAxis.None;

    private static void CheckAxis(ValidationContext ctx, GKAxis axis, string path)
    {
        if (axis == GKAxis.None) return;
        if (!ctx.DeclaredAxes.Contains(axis))
            throw new GKLayoutValidationException(
                $"layout/{path}: axis '{axis}' is not declared in the descriptor's input fields. " +
                $"Declared axes: [{string.Join(", ", ctx.DeclaredAxes)}]");
    }

    private static void CheckButton(ValidationContext ctx, int index, string path)
    {
        if (index < 0 || index >= ctx.ButtonCount)
            throw new GKLayoutValidationException(
                $"layout/{path}: button index {index} is out of range [0, {ctx.ButtonCount}).");
    }

    private static void CheckBindings(ValidationContext ctx, IEnumerable<GKButtonBinding> bindings, string path)
    {
        int i = 0;
        foreach (var b in bindings)
        {
            CheckButton(ctx, b.ButtonIndex, $"{path}[{i++}].button_index");
        }
    }

    private static void CheckHats(ValidationContext ctx, IEnumerable<GKHatBinding> hats, string path)
    {
        int i = 0;
        foreach (var h in hats)
        {
            CheckAxis(ctx, h.Axis, $"{path}[{i++}].axis");
        }
    }

    private static void CheckTriggerButton(ValidationContext ctx, GKTriggerButton? tb, string path)
    {
        if (tb is null) return;
        CheckButton(ctx, tb.ButtonIndex, $"{path}.button_index");
    }

    private static void CheckDpad(ValidationContext ctx, GKDpad? dpad, string path)
    {
        if (dpad is null) return;
        if (dpad.Encoding == GKDpadEncoding.Hat)
        {
            if (dpad.HatAxis is GKAxis ax && ax != GKAxis.None)
                CheckAxis(ctx, ax, $"{path}.hat_axis");
        }
        else
        {
            if (dpad.UpButton    is int u) CheckButton(ctx, u, $"{path}.up_button");
            if (dpad.DownButton  is int d) CheckButton(ctx, d, $"{path}.down_button");
            if (dpad.LeftButton  is int l) CheckButton(ctx, l, $"{path}.left_button");
            if (dpad.RightButton is int rr) CheckButton(ctx, rr, $"{path}.right_button");
        }
    }
}

/// <summary>Thrown when an authored <see cref="GKLayout"/> references
/// axes or buttons that don't exist in the profile's descriptor.</summary>
public sealed class GKLayoutValidationException : Exception
{
    public GKLayoutValidationException(string message) : base(message) { }
}
