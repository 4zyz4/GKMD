using System;
using System.IO;

namespace GKMD.Internal.Usbip;

/// <summary>Diagnostic trace for the Xbox One GIP persona. Writes to
/// <c>gkme_gip.log</c> next to the executable, but only when the environment
/// variable <c>GKME_GIP_DEBUG</c> is set to <c>1</c> or <c>true</c> (the
/// handshake and error lines). Best-effort: logging must never perturb the
/// device path.</summary>
internal static class GipLog
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("GKME_GIP_DEBUG") is "1" or "true";

    private static readonly object s_lock = new();
    private static readonly string s_path =
        Path.Combine(AppContext.BaseDirectory, "gkme_gip.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (s_lock)
                File.AppendAllText(s_path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    public static void Hex(string label, ReadOnlySpan<byte> data)
    {
        if (!Enabled) return;
        var sb = new System.Text.StringBuilder(label.Length + data.Length * 3 + 8);
        sb.Append(label).Append(" [").Append(data.Length).Append("] ");
        foreach (byte b in data) sb.Append(b.ToString("x2")).Append(' ');
        Write(sb.ToString());
    }
}
