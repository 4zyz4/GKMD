using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GKMD.Internal.Usbip;

/// <summary>Talks to usbip-win2's vhci host controller through its public
/// device-interface ioctl API (issue #39). The interface GUID and every
/// struct layout are <c>include/usbip/vhci.h</c>, which the driver
/// documents as a public API whose input/output data stay stable for the
/// lifetime of each IOCTL code.
///
/// <para>The layouts are NOT stable across releases, so the client speaks
/// three of them and asks the driver which one it has. 0.9.8.0 appended a
/// serial and a flag to the attach request and to each imported-device
/// row. 0.9.8.1 put a <c>location_hash</c> after <c>port</c> in the
/// location that attach, stop and every row carry, which moves busid,
/// service and host 4 bytes on. Each size and offset in
/// <see cref="Layouts"/> was checked against that tag's own header for
/// x64. The driver compares every request's size field with its own
/// <c>sizeof</c> and refuses a mismatch before acting on it
/// (vhci_ioctl.cpp in all three tags), so a probe with the wrong size is
/// harmless — which is exactly how the layout is discovered.</para>
///
/// <para>Attach uses PLUGIN_HARDWARE_ONCE (function 0x806): one attempt,
/// no background retry loop, because this SDK owns the server lifecycle
/// and a failed attach should surface as an exception, not as the
/// driver's own persistent-device machinery. Detach is PLUGOUT_HARDWARE.
/// STOP_ATTACH_ATTEMPTS exists for crash recovery: when a prior process
/// died without a plugout, the driver's socket-loss path queues re-attach
/// attempts against the dead loopback server (device.cpp detach →
/// start_attach_attempts) and this cancels them by exact location.</para>
///
/// <para>Presence of the device interface doubles as backend
/// availability detection: no usbip-win2, no interface, no backend.</para></summary>
internal static class VhciClient
{
    // include/usbip/vhci.h GUID_DEVINTERFACE_USB_HOST_CONTROLLER, renamed
    // GUID_DEVINTERFACE_USBIP_VHCI in 0.9.8.1 with the same value.
    private static readonly Guid VhciInterfaceGuid = new(0xB4030C06, 0xDC5F, 0x4FCC,
        0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0);

    // CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA),
    // the same in every tag. 0x806 is plugin_hardware_internal in 0.9.7.x
    // and plugin_hardware_once from 0.9.8.0: one attempt, no retry loop.
    private const uint PLUGIN_HARDWARE = 0x0022E000;         // fn 0x800
    private const uint PLUGOUT_HARDWARE = 0x0022E004;        // fn 0x801
    private const uint GET_IMPORTED_DEVICES = 0x0022E008;    // fn 0x802
    private const uint STOP_ATTACH_ATTEMPTS = 0x0022E014;    // fn 0x805
    private const uint PLUGIN_HARDWARE_ONCE = 0x0022E018;    // fn 0x806

    private const int BusIdSize = 32;    // consts.h BUS_ID_SIZE
    private const int ServiceSize = 32;  // NI_MAXSERV
    private const int HostSize = 1025;   // NI_MAXHOST

    // Every request starts with ULONG size, and the location that follows
    // starts with int port, so the port an attach returns is at offset 4
    // in all three layouts, and so is the first imported-device row.
    private const int HeaderSize = 4;
    // plugout_hardware is identical in every tag: base{ULONG size} + int port.
    private const int PlugoutStructSize = 8;

    // plugout_hardware.port: > 0 detaches that port; <= 0 detaches every
    // imported device. PORT_ALL (-1) is the canonical "all" value; -2
    // (PORT_ALL_CLOSEONLY) is the driver-internal variant that only closes
    // the socket.
    private const int PortAll = -1;

    // vhci.cpp set_usb_ports_cnt: MAX_TOTAL_PORTS is 255. The driver fails
    // GET_IMPORTED_DEVICES with STATUS_BUFFER_TOO_SMALL when more devices
    // are attached than the buffer holds, so a buffer this size cannot
    // make the layout probe fail for that reason.
    private const int MaxPorts = 255;

    /// <summary>One release family's layout. <c>PluginSize</c>,
    /// <c>StopSize</c> and <c>RowSize</c> are sizeof plugin_hardware,
    /// stop_attach_attempts and imported_device. <c>BusIdOffset</c> is
    /// where busid starts inside the request (after base + port); it is
    /// 8 in 0.9.8.1, where a <c>location_hash</c> follows <c>port</c>, and
    /// 4 before that. <c>WskEventsOffset</c> is where the 0.9.8.x
    /// <c>wsk_events</c> flag sits in plugin_hardware, or -1 for 0.9.7.x,
    /// whose request carries no serial and no flag (the driver's only
    /// receive path there is the MDL one).</summary>
    internal sealed record Layout(string Name, int PluginSize, int StopSize, int RowSize,
                                  int BusIdOffset, int WskEventsOffset);

    /// <summary>Newest first, which is the order the probe tries them.</summary>
    internal static readonly Layout[] Layouts =
    {
        new("0.9.8.1", PluginSize: 1124, StopSize: 1108, RowSize: 1132, BusIdOffset: 8, WskEventsOffset: 1120),
        new("0.9.8.0", PluginSize: 1120, StopSize: 1104, RowSize: 1128, BusIdOffset: 4, WskEventsOffset: 1116),
        new("0.9.7.x", PluginSize: 1100, StopSize: 1104, RowSize: 1108, BusIdOffset: 4, WskEventsOffset: -1),
    };

    /// <summary>True when usbip-win2's vhci controller is present and
    /// running. Requires no elevation.</summary>
    public static bool IsAvailable() => TryGetInterfacePath() != null;

    public static string? TryGetInterfacePath()
    {
        var guid = VhciInterfaceGuid;
        uint cr = CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (cr != 0 || len <= 1) return null;
        var buf = new char[len];
        cr = CM_Get_Device_Interface_ListW(ref guid, null, buf, len,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (cr != 0) return null;
        int end = Array.IndexOf(buf, '\0');
        if (end <= 0) return null;
        return new string(buf, 0, end);
    }

    private static SafeHandleWrapper Open()
    {
        string path = TryGetInterfacePath()
            ?? throw new InvalidOperationException(
                "The virtual USB host controller is not present. It ships inside GKMD.Core.dll " +
                "and installs on first use; see UsbipDriverInstaller.EnsureInstalled.");
        IntPtr h = CreateFileW(path, 0xC0000000 /* GENERIC_READ|WRITE */, 0, IntPtr.Zero,
            3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile('{path}') failed.");
        return new SafeHandleWrapper(h);
    }

    private static void WriteLocation(byte[] buf, int offset, string busid, string service, string host)
    {
        Encoding.UTF8.GetBytes(busid).AsSpan(0, Math.Min(busid.Length, BusIdSize - 1))
            .CopyTo(buf.AsSpan(offset));
        Encoding.UTF8.GetBytes(service).AsSpan(0, Math.Min(service.Length, ServiceSize - 1))
            .CopyTo(buf.AsSpan(offset + BusIdSize));
        Encoding.UTF8.GetBytes(host).AsSpan(0, Math.Min(host.Length, HostSize - 1))
            .CopyTo(buf.AsSpan(offset + BusIdSize + ServiceSize));
    }

    /// <summary>Find the layout the installed driver speaks. Every tag
    /// answers GET_IMPORTED_DEVICES only when the size field equals its own
    /// sizeof(get_imported_devices), the header plus one row, and the call
    /// changes nothing, so the first candidate that succeeds is the one.
    /// The rows it returned are handed back for <see cref="GetImportedDevices"/>.</summary>
    internal static Layout Probe(IntPtr handle, out byte[] rows, out uint written)
    {
        int lastError = 0;
        foreach (var layout in Layouts)
        {
            var buf = new byte[HeaderSize + layout.RowSize * MaxPorts];
            // The driver validates r->size against sizeof(get_imported_devices),
            // the header plus ONE ANYSIZE_ARRAY row, not the caller's buffer
            // length (vhci_ioctl.cpp get_imported_devices).
            BitConverter.GetBytes((uint)(HeaderSize + layout.RowSize)).CopyTo(buf, 0);
            if (DeviceIoControl(handle, GET_IMPORTED_DEVICES, buf, (uint)buf.Length,
                    buf, (uint)buf.Length, out written, IntPtr.Zero))
            {
                rows = buf;
                return layout;
            }
            lastError = Marshal.GetLastWin32Error();
        }
        throw new InvalidOperationException(
            "The usbip-win2 host controller accepted none of the request layouts this SDK knows " +
            $"(0.9.8.1, 0.9.8.0, 0.9.7.x). Last error 0x{lastError:X8}.");
    }

    /// <summary>The layout the installed driver speaks, for diagnostics.
    /// Null when no host controller answers any known layout.</summary>
    public static string? InstalledLayout()
    {
        try
        {
            using var h = Open();
            return Probe(h.Handle, out _, out _).Name;
        }
        catch { return null; }
    }

    /// <summary>Attach one exported device. Blocks until the driver has
    /// connected to the server, completed the import handshake, and
    /// plugged the UDE device in. Returns the vhci port for detach.
    ///
    /// <para><paramref name="receiveMode"/> selects the driver's network
    /// receive path on the 0.9.8.x layouts (<c>wsk_events</c>); the 0.9.7.x
    /// layout has no such field and always uses the MDL path. The serial
    /// field is left empty, which the driver accepts and which disables its
    /// serial-based descriptor patching.</para></summary>
    public static int Attach(string host, int port, string busid,
                             UsbipReceiveMode receiveMode = UsbipReceiveMode.LowLatency)
    {
        using var h = Open();
        var layout = Probe(h.Handle, out _, out _);
        var buf = new byte[layout.PluginSize];
        BitConverter.GetBytes((uint)layout.PluginSize).CopyTo(buf, 0);
        WriteLocation(buf, HeaderSize + layout.BusIdOffset, busid, port.ToString(), host);
        if (layout.WskEventsOffset >= 0)
            buf[layout.WskEventsOffset] = receiveMode == UsbipReceiveMode.LowLatency ? (byte)1 : (byte)0;

        if (!DeviceIoControl(h.Handle, PLUGIN_HARDWARE_ONCE, buf, (uint)buf.Length,
                buf, (uint)buf.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"usbip-win2 {layout.Name} attach of {busid} at {host}:{port} failed.");

        int vhciPort = BitConverter.ToInt32(buf, HeaderSize);
        if (vhciPort < 1)
            throw new InvalidOperationException($"usbip-win2 attach of {busid} returned port {vhciPort}.");
        return vhciPort;
    }

    /// <summary>Detach the device on a vhci port. portOrZero &lt;= 0
    /// detaches every imported device (used only by explicit cleanup);
    /// 0 is normalized to PORT_ALL, which is what the driver expects.</summary>
    public static void Detach(int portOrZero)
    {
        using var h = Open();
        var buf = new byte[PlugoutStructSize];
        BitConverter.GetBytes((uint)PlugoutStructSize).CopyTo(buf, 0);
        BitConverter.GetBytes(portOrZero > 0 ? portOrZero : PortAll).CopyTo(buf, 4);
        DeviceIoControl(h.Handle, PLUGOUT_HARDWARE, buf, (uint)buf.Length,
            IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    /// <summary>Wait, bounded, until a vhci port no longer appears in the
    /// imported-device list. PLUGOUT_HARDWARE is synchronous, but PnP's
    /// teardown of the emulated device's child functions (the UAC/HID
    /// devnodes) is not; re-attaching onto the same port before that
    /// completes makes Windows merge the new persona onto the dying
    /// devnode, which is how a pad with no audio interface inherits a
    /// stale USB Audio endpoint when switching modes repeatedly.
    /// Returns true when the port is gone (or the driver is unavailable,
    /// which degrades to the previous best-effort behavior).</summary>
    public static bool WaitForPortDetached(int port, int timeoutMs)
    {
        if (port <= 0) return true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool present = false;
            foreach (var row in GetImportedDevices())
            {
                if (row.Port == port) { present = true; break; }
            }
            if (!present) return true;
            System.Threading.Thread.Sleep(40);
        }
        return false;
    }

    /// <summary>Cancel the driver's background re-attach attempts for one
    /// exact location. Safe to call when none exist.</summary>
    public static void StopAttachAttempts(string host, int port, string busid)
    {
        try
        {
            using var h = Open();
            var layout = Probe(h.Handle, out _, out _);
            var buf = new byte[layout.StopSize];
            BitConverter.GetBytes((uint)layout.StopSize).CopyTo(buf, 0);
            WriteLocation(buf, HeaderSize + layout.BusIdOffset, busid, port.ToString(), host);
            DeviceIoControl(h.Handle, STOP_ATTACH_ATTEMPTS, buf, (uint)buf.Length,
                buf, (uint)buf.Length, out _, IntPtr.Zero);
        }
        catch { /* cleanup path; absence of the driver is fine */ }
    }

    /// <summary>True when the installed vhci driver accepts a request
    /// layout this build knows (0.9.8.1, 0.9.8.0 or 0.9.7.x), so composite
    /// personas can attach against it right now. A pre-0.9.8.1 driver
    /// rejects a request whose <c>size</c> field is not its own
    /// <c>sizeof</c> (USBIP_ERROR_ABI), so the probe proves both presence
    /// and that this build can speak to it. Used by
    /// <see cref="UsbipDriverInstaller"/> to decide whether the bundled
    /// installer must be deployed or an older usbip-win2 upgraded.</summary>
    public static bool IsCurrentAbi()
    {
        try
        {
            using var h = Open();
            Probe(h.Handle, out _, out _);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Rows from GET_IMPORTED_DEVICES: (port, busid, service,
    /// host). Used by crash recovery to find and plug out stale imports
    /// that point at this SDK's server range.</summary>
    public static List<(int Port, string BusId, string Service, string Host)> GetImportedDevices()
    {
        var result = new List<(int, string, string, string)>();
        try
        {
            using var h = Open();
            // The probe's successful call is the listing: a header of ULONG
            // size, then one imported_device row per attached device.
            var layout = Probe(h.Handle, out byte[] buf, out uint written);
            int rows = written >= HeaderSize ? (int)((written - HeaderSize) / layout.RowSize) : 0;
            for (int i = 0; i < rows; i++)
            {
                int off = HeaderSize + i * layout.RowSize;
                int port = BitConverter.ToInt32(buf, off);
                int busidAt = off + layout.BusIdOffset;
                string busid = ReadUtf8(buf, busidAt, BusIdSize);
                string service = ReadUtf8(buf, busidAt + BusIdSize, ServiceSize);
                string host = ReadUtf8(buf, busidAt + BusIdSize + ServiceSize, HostSize);
                if (port >= 1) result.Add((port, busid, service, host));
            }
        }
        catch { /* detection is best-effort */ }
        return result;
    }

    private static string ReadUtf8(byte[] buf, int offset, int max)
    {
        int end = Array.IndexOf(buf, (byte)0, offset, max);
        int len = end < 0 ? max : end - offset;
        return Encoding.UTF8.GetString(buf, offset, len);
    }

    private sealed class SafeHandleWrapper : IDisposable
    {
        public IntPtr Handle { get; }
        public SafeHandleWrapper(IntPtr h) => Handle = h;
        public void Dispose() => CloseHandle(Handle);
    }

    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid interfaceClassGuid,
        string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId,
        [Out] char[] buffer, uint bufferLen, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr sa,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
        byte[]? inBuffer, uint inSize, byte[]? outBuffer, uint outSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
        byte[]? inBuffer, uint inSize, IntPtr outBuffer, uint outSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}

/// <summary>Network receive path the usbip UDE driver uses for an imported
/// device (usbip-win2 0.9.8.x <c>plugin_hardware.wsk_events</c>). Ignored
/// on the 0.9.7.x layout, whose request has no such field.</summary>
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
