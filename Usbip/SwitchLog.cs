using System;
using System.IO;
using System.Text;

namespace GKMD.Internal.Usbip;

/// <summary>Diagnostic trace for the Switch Pro / Joy-Con personas. Writes to
/// <c>gkme_switch.log</c> beside the executable (falling back to the temp
/// directory) only when <c>GKMD_SWITCH_DEBUG</c> or <c>GKME_SWITCH_DEBUG</c> is
/// <c>1</c> or <c>true</c>. Best-effort: logging must never perturb the device
/// path, and every caller guards with <see cref="Enabled"/> so a disabled log
/// costs one bool test and no string formatting.</summary>
internal static class SwitchLog
{
    public static readonly bool Enabled =
        IsOn("GKMD_SWITCH_DEBUG") || IsOn("GKME_SWITCH_DEBUG") || MarkerExists();

    private static bool MarkerExists()
    {
        try
        {
            // A zero-byte marker is easier than an environment variable when
            // the host application is launched from a shortcut or store.
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "switch_debug.txt"));
        }
        catch { return false; }
    }

    private static readonly object s_lock = new();
    private static string? s_path;

    private static bool IsOn(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return v is not null
            && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolvePath()
    {
        if (s_path != null) return s_path;
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "gkme_switch.log");
            using (File.AppendText(p)) { }
            return s_path = p;
        }
        catch
        {
            string p = Path.Combine(Path.GetTempPath(), "gkme_switch.log");
            return s_path = p;
        }
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            string path = ResolvePath();
            lock (s_lock)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    /// <summary>True at most once per <paramref name="intervalMs"/> for a
    /// call site that owns its own timestamp. Used to sample a 15 ms frame or
    /// 60 Hz read loop instead of logging every frame.</summary>
    public static bool Due(ref long lastMs, int intervalMs = 1000)
    {
        if (!Enabled) return false;
        long now = Environment.TickCount64;
        if (now - lastMs < intervalMs) return false;
        lastMs = now;
        return true;
    }

    public static void Hex(string label, ReadOnlySpan<byte> data, int max = 32)
    {
        if (!Enabled) return;
        int n = Math.Min(data.Length, max);
        var sb = new StringBuilder(label.Length + n * 3 + 24);
        sb.Append(label).Append(" [").Append(data.Length).Append(']');
        if (n < data.Length) sb.Append(" (first ").Append(n).Append(')');
        sb.Append(' ');
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2")).Append(' ');
        Write(sb.ToString());
    }

    /// <summary>The path chosen for this run; written in the device's create
    /// line so the user knows where to look.</summary>
    public static string PathForDisplay => Enabled ? ResolvePath() : "(disabled)";
}
