using System;

namespace GKMD.Internal.Usbip;

/// <summary>Raised when the bundled usbip-win2 transport is absent or too
/// old and must be installed/upgraded. Installing is a manual choice now:
/// the UI shows a prompt whose install button releases and runs the bundled
/// installer, rather than the SDK installing silently mid-create.</summary>
public sealed class UsbipInstallRequiredException : Exception
{
    /// <summary>True when a usbip-win2 is already present but at the older
    /// ABI (an upgrade), false when nothing is installed yet.</summary>
    public bool IsUpgrade { get; }

    public UsbipInstallRequiredException(bool isUpgrade, string message) : base(message)
        => IsUpgrade = isUpgrade;
}

/// <summary>Outcome of <see cref="UsbipDriverInstaller.Install"/>.</summary>
public enum UsbipInstallResult
{
    /// <summary>Installed and usable right now.</summary>
    Success,
    /// <summary>Installed, but Windows will not load the new kernel image
    /// until the next boot.</summary>
    RebootRequired,
    /// <summary>The user cancelled, or the installer did not complete.</summary>
    Cancelled,
}
