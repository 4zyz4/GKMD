using System;

namespace GKMD.Internal.Usbip;

/// <summary>Raised when a usbip-win2 driver upgrade has been applied but
/// Windows will not load the new kernel image until the next boot, so no
/// further reinstall in this session can make attach work.
///
/// <para>The UI layers turn this into a "please reboot" prompt instead of
/// treating it as a failure.</para></summary>
public sealed class UsbipRebootRequiredException : Exception
{
    public UsbipRebootRequiredException(string message) : base(message) { }
}
