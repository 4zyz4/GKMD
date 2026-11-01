namespace GKMD.Internal.Usbip;

/// <summary>Network receive path the usbip host controller uses for an
/// imported device (usbip-win2 0.9.8.x <c>plugin_hardware.wsk_events</c>).
/// Ignored on the 0.9.7.x layout, which has no such field, and ignored on
/// Linux, where the in-kernel vhci-hcd driver owns the socket and its
/// receive path is not selectable from user mode.</summary>
internal enum UsbipReceiveMode
{
    /// <summary>Zero-copy: the driver receives straight into the pending
    /// URB's MDLs through WSK kernel functions; the only mode before
    /// 0.9.8.0.</summary>
    ZeroCopy = 0,

    /// <summary>Low-latency: the driver receives through WSK event
    /// callbacks and parses the stream from a ring buffer. Better suited
    /// to small, high-frequency reports.</summary>
    LowLatency = 1,
}
