using System;

namespace GKMD.Internal.Usbip;

/// <summary>Raised when the platform's USB/IP transport is absent or unusable
/// and must be installed or loaded. On Windows that means the bundled
/// usbip-win2 transport is missing or too old; on Linux, that
/// <c>vhci_hcd</c> is not loaded. Installing is a manual choice: the UI shows
/// a prompt whose install button runs the bundled installer (Windows) or
/// <c>modprobe vhci_hcd</c> (Linux), rather than the SDK installing silently
/// mid-create.</summary>
public sealed class UsbipInstallRequiredException : Exception
{
    /// <summary>True when a usbip-win2 is already present but at the older
    /// ABI (an upgrade), false when nothing is installed yet. Always false on
    /// Linux, which has no versioned transport.</summary>
    public bool IsUpgrade { get; }

    public UsbipInstallRequiredException(bool isUpgrade, string message) : base(message)
        => IsUpgrade = isUpgrade;
}

/// <summary>Outcome of <see cref="UsbipDriverInstaller.Install"/>.</summary>
public enum UsbipInstallResult
{
    /// <summary>Installed and usable right now.</summary>
    Success,
    /// <summary>Installed on Windows, but the new kernel image is not loaded
    /// until the next boot. Linux never returns this: loading a module takes
    /// effect immediately.</summary>
    RebootRequired,
    /// <summary>The user cancelled, or the install did not complete.</summary>
    Cancelled,
}
