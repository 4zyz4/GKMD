#if !GKMD_WINDOWS

using System;
using System.Diagnostics;
using System.Threading;

namespace GKMD.Internal.Usbip;

/// <summary>Prepares Linux's USB/IP host controller, <c>vhci_hcd</c>.
///
/// <para>Linux needs no driver of GKMD's own and no downloaded installer:
/// mainline ships both halves of USB/IP, and <c>vhci_hcd</c> is the virtual
/// host controller that enumerates an imported device as a real one. The
/// only preparation is making sure the module is loaded
/// (<c>modprobe vhci_hcd</c>), which several distributions already do at
/// boot and which always needs root.</para>
///
/// <para>The API mirrors the Windows installer on purpose:
/// <see cref="EnsureInstalled"/> probes and reports what is missing without
/// changing the machine, <see cref="Install"/> performs the one privileged
/// step, and <see cref="IsInstalled"/> is a side-effect-free presence
/// check. There is no versioned driver package, so no upgrade or reboot
/// path exists, and <see cref="BundledArchitecture"/> is always null.</para></summary>
internal static partial class UsbipDriverInstaller
{
    /// <summary>There is no versioned transport to report: the driver is
    /// the running kernel's.</summary>
    public const string Version = "in-kernel";

    /// <summary>True when <c>vhci_hcd</c> is loaded and its sysfs interface
    /// is present. Needs no elevation.</summary>
    public static bool IsInstalled => VhciClient.IsAvailable();

    /// <summary>This build embeds no installer, so there is no bundled
    /// architecture. Kept for parity with the Windows build.</summary>
    internal static string? BundledArchitecture => null;

    /// <summary>Probe the transport and report what is missing. Never
    /// changes the machine: when <c>vhci_hcd</c> is not loaded this throws
    /// <see cref="UsbipInstallRequiredException"/>, which a UI turns into a
    /// prompt whose button calls <see cref="Install"/>. Idempotent and safe
    /// to call on every composite create.</summary>
    /// <param name="progress">Optional status callback.</param>
    public static bool EnsureInstalled(Action<string>? progress = null)
    {
        if (VhciClient.IsAvailable() && VhciClient.IsCurrentAbi())
        {
            progress?.Invoke("Linux vhci-hcd is loaded.");
            return true;
        }

        throw new UsbipInstallRequiredException(false,
            "The Linux vhci-hcd USB/IP host controller is not loaded. Load it with " +
            "'sudo modprobe vhci_hcd' (it is part of mainline; no extra driver is needed), " +
            "then create the controller again.");
    }

    /// <summary>Load <c>vhci_hcd</c>. Called from the install prompt after
    /// the user clicks install; never runs on its own.
    ///
    /// <para><c>modprobe</c> needs root. When this process already is root
    /// (the normal case for a gamepad engine that attaches vhci devices)
    /// the module is loaded directly. Otherwise an interactive install goes
    /// through <c>pkexec</c> so the desktop can authenticate the user; a
    /// silent install never prompts and reports
    /// <see cref="UsbipInstallResult.Cancelled"/> instead.</para></summary>
    public static UsbipInstallResult Install(Action<string>? progress = null, bool interactive = true)
    {
        if (VhciClient.IsAvailable())
        {
            progress?.Invoke("Linux vhci-hcd is already loaded.");
            return UsbipInstallResult.Success;
        }

        progress?.Invoke("Loading Linux's vhci-hcd USB/IP host controller...");
        bool loaded = RunModprobe(privileged: false, out string detail);
        if (!loaded && interactive)
        {
            progress?.Invoke("Requesting privilege to load vhci_hcd...");
            loaded = RunModprobe(privileged: true, out detail);
        }

        if (loaded && WaitForVhci())
        {
            progress?.Invoke("Linux vhci-hcd is ready.");
            return UsbipInstallResult.Success;
        }

        progress?.Invoke(string.IsNullOrWhiteSpace(detail)
            ? "vhci_hcd is still not loaded."
            : "vhci_hcd could not be loaded: " + detail);
        return UsbipInstallResult.Cancelled;
    }

    /// <summary>Run modprobe, directly first (works when root, which needs
    /// no helper) and through pkexec when asked to elevate. The module file
    /// is <c>vhci-hcd.ko</c>; kmod treats the underscore form as the same
    /// name, and both spellings appear in distribution docs, so try both.</summary>
    private static bool RunModprobe(bool privileged, out string detail)
    {
        detail = string.Empty;
        foreach (string module in new[] { "vhci-hcd", "vhci_hcd" })
        {
            if (privileged)
            {
                // pkexec sanitises PATH but keeps the standard sbin
                // directories, where modprobe lives everywhere.
                if (RunCommand("pkexec", "modprobe " + module, out detail))
                    return true;
                continue;
            }

            // The module loader's path is not on a regular user's PATH on
            // some distributions, so try the standard locations as well.
            foreach (string exe in new[] { "modprobe", "/usr/sbin/modprobe", "/sbin/modprobe" })
                if (RunCommand(exe, module, out detail))
                    return true;
        }
        return false;
    }

    /// <summary>The module load is synchronous, but the platform device and
    /// its sysfs files are created by the driver's probe, so give that a
    /// short, bounded moment to appear.</summary>
    private static bool WaitForVhci(int timeoutMs = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutScale.Apply(timeoutMs))
        {
            if (VhciClient.IsAvailable()) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool RunCommand(string fileName, string arguments, out string detail)
    {
        detail = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                detail = $"{fileName} could not be started.";
                return false;
            }

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(TimeoutScale.Apply(30_000)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                detail = $"{fileName} did not finish within the timeout.";
                return false;
            }

            if (p.ExitCode != 0)
            {
                detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}

#endif
