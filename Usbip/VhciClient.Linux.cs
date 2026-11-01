#if !GKMD_WINDOWS

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GKMD.Internal.Usbip;

/// <summary>Drives the in-kernel Linux USB/IP client, <c>vhci_hcd</c>, and
/// its public sysfs interface. Linux needs no driver of GKMD's own: mainline
/// has both halves of USB/IP, and vhci_hcd is the virtual host controller
/// that enumerates a remote device as a real one. The kernel's contract is
/// user-space driven — the client program connects to the server, performs
/// the OP_REQ_IMPORT handshake, and then hands the connected socket to the
/// kernel by writing
///
/// <code>port sockfd devid speed</code>
///
/// to <c>/sys/devices/platform/vhci_hcd.0/attach</c>. The kernel looks the
/// descriptor up in the writing process and runs the URB traffic over that
/// socket from then on (drivers/usb/usbip/vhci_sysfs.c).
///
/// <para>Everything below is that same contract, spelled out in managed
/// code so the SDK attaches its own server without shelling out to
/// <c>usbip</c>. A user who attaches by hand with the distro's
/// <c>usbip --tcp-port &lt;server port&gt; attach -r 127.0.0.1 -b 1-N</c>
/// talks to the same server and works too: the server admits any root
/// import (see <c>UsbipServer</c>).</para>
///
/// <para>The device status file is
/// <c>hub port sta spd dev sockfd local_busid</c>, one row per port of
/// every controller, with <c>VDEV_ST_NULL</c> (4) meaning the port is free.
/// The remote location of an import — the host, server port and remote bus
/// id the Windows client reads back from its driver — is kept by the
/// <c>usbip</c> tool in <c>/var/run/vhci_hcd/port&lt;N&gt;</c>; this client
/// writes the same record so location-based detach and the stale sweep work
/// exactly as they do on Windows.</para>
///
/// <para>Attaching requires root (the sysfs files are root-writable), the
/// same elevation the Windows backend needs for its driver install and
/// attach calls.</para></summary>
internal static partial class VhciClient
{
    private const string DefaultSysfs = "/sys/devices/platform/vhci_hcd.0";

    /// <summary>Where the <c>usbip</c> tool keeps one record per attached
    /// port: <c>host service busid</c> (tools/usb/usbip/src/usbip_attach.c
    /// VHCI_STATE_PATH).</summary>
    private const string StatePath = "/var/run/vhci_hcd";

    // include/uapi/linux/usbip.h usbip_device_status: VDEV_ST_NULL is a port
    // with no remote device; VDEV_ST_USED / NOTASSIGNED are imports.
    private const int VdevStNull = 4;

    // enum usb_device_speed: USB_SPEED_SUPER and above live on the SuperSpeed
    // root hub.
    private const uint UsbSpeedSuper = 5;

    // fcntl(2) on Linux.
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;

    private static string? s_sysfsPath;
    private static readonly object s_lock = new();

    /// <summary>Locations this process attached, by vhci port. The usbip
    /// record file is the cross-process source of truth; this map keeps
    /// detach-by-location working even when that file cannot be written
    /// (a locked-down /var/run).</summary>
    private static readonly Dictionary<int, (string Host, int Service, string BusId)> s_attached = new();

    private readonly record struct StatusRow(bool Super, int Port, int Status, uint Devid, string LocalBusId);

    /// <summary>True when vhci-hcd is loaded and its sysfs interface is
    /// present. Requires no elevation.</summary>
    public static bool IsAvailable() => TryGetSysfsPath() != null;

    /// <summary>The vhci-hcd sysfs directory, or null when the module is not
    /// loaded. Linux's counterpart of the Windows device-interface path, kept
    /// for API parity.</summary>
    public static string? TryGetInterfacePath() => TryGetSysfsPath();

    /// <summary>The sysfs directory of the virtual host controller. The
    /// module creates <c>vhci_hcd.0</c>; the scan is the fallback for a
    /// kernel whose platform device is named differently.</summary>
    private static string? TryGetSysfsPath()
    {
        string? cached = s_sysfsPath;
        if (cached != null && File.Exists(Path.Combine(cached, "attach"))) return cached;

        lock (s_lock)
        {
            if (s_sysfsPath != null && File.Exists(Path.Combine(s_sysfsPath, "attach")))
                return s_sysfsPath;

            string[] candidates;
            try
            {
                candidates = Directory.GetDirectories("/sys/devices/platform", "vhci_hcd*");
            }
            catch { candidates = Array.Empty<string>(); }

            string? found = null;
            if (File.Exists(Path.Combine(DefaultSysfs, "attach"))) found = DefaultSysfs;
            else
                foreach (string dir in candidates)
                    if (File.Exists(Path.Combine(dir, "attach"))) { found = dir; break; }

            s_sysfsPath = found;
            return found;
        }
    }

    private static string RequireSysfsPath() =>
        TryGetSysfsPath() ?? throw new InvalidOperationException(
            "Linux's vhci-hcd USB/IP host controller is not loaded. Load it with " +
            "'sudo modprobe vhci_hcd' (it ships in mainline; no extra driver is needed), " +
            "then create the controller again.");

    /// <summary>Every port row of every controller. <c>status</c> covers
    /// controller 0, <c>status.N</c> the rest.</summary>
    private static List<StatusRow> ReadStatus(string sysfs)
    {
        var rows = new List<StatusRow>();
        string[] files;
        try { files = Directory.GetFiles(sysfs, "status*"); }
        catch { return rows; }
        Array.Sort(files, StringComparer.Ordinal);

        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            foreach (string line in text.Split('\n'))
            {
                // hub port sta spd dev sockfd local_busid
                string[] p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 7) continue;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) continue;
                if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int status)) continue;
                if (!uint.TryParse(p[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint devid)) continue;
                rows.Add(new StatusRow(p[0].StartsWith("ss", StringComparison.Ordinal), port, status, devid, p[6]));
            }
        }
        return rows;
    }

    /// <summary>Read one port's remote location record, if any.</summary>
    private static (string Host, int Service, string BusId)? ReadRecord(int port)
    {
        try
        {
            string text = File.ReadAllText(Path.Combine(StatePath, "port" + port.ToString(CultureInfo.InvariantCulture)));
            string[] p = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) return null;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int service)) return null;
            return (p[0], service, p[2]);
        }
        catch { return null; }
    }

    /// <summary>Best effort: the record is informational (the <c>usbip</c>
    /// tool prints it) and the source of location-based detach.</summary>
    private static void WriteRecord(int port, string host, int service, string busid)
    {
        try
        {
            Directory.CreateDirectory(StatePath);
            File.WriteAllText(Path.Combine(StatePath, "port" + port.ToString(CultureInfo.InvariantCulture)),
                $"{host} {service} {busid}\n");
        }
        catch { /* location still lives in s_attached */ }
    }

    private static void DeleteRecord(int port)
    {
        try { File.Delete(Path.Combine(StatePath, "port" + port.ToString(CultureInfo.InvariantCulture))); }
        catch { }
    }

    /// <summary>Attach one exported device through vhci-hcd. Blocks until
    /// the kernel owns the socket and the virtual device is connected.
    /// Returns the vhci port, which detach and status use.
    ///
    /// <para><paramref name="receiveMode"/> is the usbip-win2 WSK receive
    /// path and is ignored here: the Linux kernel owns both ends of the
    /// transfer and offers no such choice.</para></summary>
    public static int Attach(string host, int port, string busid,
                             UsbipReceiveMode receiveMode = UsbipReceiveMode.LowLatency)
    {
        string sysfs = RequireSysfsPath();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        socket.Connect(host, port);

        // OP_REQ_IMPORT: op_common (version, code, status) + fixed 32-byte
        // bus id. The server answers op_common + the 312-byte device block.
        Span<byte> request = stackalloc byte[8 + UsbipProtocol.BusIdSize];
        request.Clear();
        UsbipProtocol.WriteOpCommon(request, UsbipProtocol.OpReqImport, 0);
        WriteFixedUtf8(request.Slice(8, UsbipProtocol.BusIdSize), busid);
        SendAll(socket, request);

        Span<byte> header = stackalloc byte[8];
        ReadExactly(socket, header);
        ushort replyCode = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        uint status = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (replyCode != UsbipProtocol.OpRepImport)
            throw new InvalidOperationException(
                $"The USB/IP server answered import of {busid} with op code 0x{replyCode:X4}, not an import reply.");
        if (status != UsbipProtocol.StOk)
            throw new InvalidOperationException(
                $"The USB/IP server refused import of {busid} (status {status}). " +
                "It is exported by another connection, or the bus id is gone.");

        Span<byte> device = stackalloc byte[UsbipProtocol.UsbipUsbDeviceSize];
        ReadExactly(socket, device);
        string replyBusId = UsbipProtocol.ReadFixedUtf8(
            device.Slice(UsbipProtocol.DevPathMax, UsbipProtocol.BusIdSize));
        if (!string.Equals(replyBusId, busid, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The USB/IP server answered import of {busid} with device {replyBusId}.");
        int busnumAt = UsbipProtocol.DevPathMax + UsbipProtocol.BusIdSize;
        uint busnum = BinaryPrimitives.ReadUInt32BigEndian(device[busnumAt..]);
        uint devnum = BinaryPrimitives.ReadUInt32BigEndian(device[(busnumAt + 4)..]);
        uint speed = BinaryPrimitives.ReadUInt32BigEndian(device[(busnumAt + 8)..]);
        if (speed == 0) speed = 3; // USB_SPEED_UNKNOWN from a server that omits it
        uint devid = (busnum << 16) | devnum;

        // The kernel's usbip client wants a blocking socket; the managed
        // socket may have O_NONBLOCK set. Clear it before the handoff so
        // kernel_sendmsg/kernel_recvmsg wait instead of returning EAGAIN.
        int fd = socket.SafeHandle.DangerousGetHandle().ToInt32();
        ClearNonBlocking(fd);

        int vhciPort = AttachToKernel(sysfs, fd, devid, speed, speed >= UsbSpeedSuper);
        lock (s_lock) s_attached[vhciPort] = (host, port, busid);
        WriteRecord(vhciPort, host, port, busid);
        return vhciPort;
    }

    /// <summary>Pick a free port on the root hub that matches the device's
    /// speed and hand the socket over. A port another attach took between
    /// the status read and the write answers EBUSY; pick the next one. Ports
    /// a just-disposed device left in their disconnect transition come back
    /// to <c>VDEV_ST_NULL</c> shortly, so the search waits, bounded, instead
    /// of failing a fast mode switch.</summary>
    private static int AttachToKernel(string sysfs, int fd, uint devid, uint speed, bool super)
    {
        string attachPath = Path.Combine(sysfs, "attach");
        int budgetMs = TimeoutScale.Apply(3_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        int lastPort = -1;
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            int freePort = FindFreePort(sysfs, super);
            if (freePort < 0)
            {
                Thread.Sleep(40);
                continue;
            }

            lastPort = freePort;
            try
            {
                WriteSysfs(attachPath, $"{freePort} {fd} {devid} {speed}");
                return freePort;
            }
            catch (UnauthorizedAccessException ex)
            {
                // EPERM: vhci sysfs is root-writable and this process is not.
                last = ex;
                break;
            }
            catch (IOException ex)
            {
                // EBUSY means another attach won the port; retry with a
                // fresh status read.
                last = ex;
                Thread.Sleep(20);
            }
        }

        if (last == null)
            throw new InvalidOperationException(
                $"No free vhci-hcd {(super ? "SuperSpeed" : "high-speed")} port. " +
                "Detach another imported device, or raise the module's port count.");
        throw new InvalidOperationException(
            $"vhci-hcd refused the attach request for port {lastPort} " +
            $"(devid {devid:X8}, speed {speed}).", last);
    }

    private static int FindFreePort(string sysfs, bool super)
    {
        foreach (var row in ReadStatus(sysfs))
        {
            if (row.Super != super || row.Status != VdevStNull) continue;
            return row.Port;
        }
        return -1;
    }

    /// <summary>Detach the device on a vhci port. A negative value detaches
    /// every imported device (used only by explicit cleanup); unlike Windows,
    /// 0 is a real vhci port on Linux (the global numbering starts there), so
    /// only negative values mean "all". Waits, bounded, for the kernel to
    /// finish the disconnect so a following create cannot race a port that is
    /// still being torn down.</summary>
    public static void Detach(int portOrZero)
    {
        string? sysfs = TryGetSysfsPath();
        if (sysfs == null) return;
        if (portOrZero < 0)
        {
            foreach (var row in ReadStatus(sysfs))
                if (row.Status != VdevStNull) Detach(row.Port);
            return;
        }
        bool wrote = true;
        try { WriteSysfs(Path.Combine(sysfs, "detach"), portOrZero.ToString(CultureInfo.InvariantCulture)); }
        catch { wrote = false; /* best effort, like the Windows PLUGOUT call */ }
        // The kernel's disconnect runs on the hub's event thread. Wait for
        // the port to report VDEV_ST_NULL so the caller's next attach sees a
        // truly free port, not one whose old device is still going away.
        if (wrote) WaitForPortDetached(portOrZero, TimeoutScale.Apply(2_000));
        lock (s_lock) s_attached.Remove(portOrZero);
        DeleteRecord(portOrZero);
    }

    /// <summary>Wait, bounded, until a vhci port is free again. Detach is
    /// asynchronous on Linux (the disconnect runs on the hub's event
    /// thread), so a caller that is about to reuse the port waits here.
    /// Returns true when the port is gone (or vhci-hcd is unavailable,
    /// which degrades to best effort).</summary>
    public static bool WaitForPortDetached(int port, int timeoutMs)
    {
        if (port < 0) return true;
        string? sysfs = TryGetSysfsPath();
        if (sysfs == null) return true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool used = false;
            foreach (var row in ReadStatus(sysfs))
            {
                if (row.Port == port && row.Status != VdevStNull) { used = true; break; }
            }
            if (!used) return true;
            Thread.Sleep(40);
        }
        return false;
    }

    /// <summary>Whether an import's service string names this port. The
    /// record carries the server port as text, so compare it as a number.</summary>
    public static bool SamePort(string service, int port) =>
        int.TryParse(service, out int parsed) && parsed == port;

    /// <summary>The vhci port of one exact location's import, or -1 when
    /// nothing is imported there. Linux's global port numbering starts at 0,
    /// so 0 itself is a valid port and cannot double as "not found".</summary>
    public static int FindLocation(string host, int port, string busid)
    {
        foreach (var row in GetImportedDevices())
            if (row.Host == host && SamePort(row.Service, port) && row.BusId == busid) return row.Port;
        return -1;
    }

    /// <summary>Detach the imports of one exact location, wherever the
    /// kernel connected them. Returns how many it detached.</summary>
    public static int DetachLocation(string host, int port, string busid)
    {
        int detached = 0;
        foreach (var row in GetImportedDevices())
        {
            if (row.Host != host || !SamePort(row.Service, port) || row.BusId != busid) continue;
            Detach(row.Port);
            detached++;
        }
        return detached;
    }

    /// <summary>Wait, bounded, until one exact location is gone.</summary>
    public static bool WaitForLocationDetached(string host, int port, string busid, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (FindLocation(host, port, busid) < 0) return true;
            Thread.Sleep(40);
        }
        return false;
    }

    /// <summary>Linux has no background re-attach attempts: when a
    /// connection drops, vhci-hcd disconnects the device and frees the port.
    /// Kept so the crash-recovery code runs unchanged.</summary>
    public static void StopAttachAttempts(string host, int port, string busid) { }

    /// <summary>See <see cref="StopAttachAttempts(string,int,string)"/>.</summary>
    public static int StopAttachAttempts(string host,
        IReadOnlyCollection<(int Port, string BusId)> locations) => 0;

    /// <summary>True when vhci-hcd is present. Linux has one in-tree
    /// implementation and no request-layout generations to probe.</summary>
    public static bool IsCurrentAbi() => IsAvailable();

    /// <summary>Rows from the status files, with the location from the
    /// usbip record (or this process's attach map). Used by crash recovery
    /// to find and plug out stale imports that point at this SDK's server
    /// range.</summary>
    public static List<(int Port, string BusId, string Service, string Host)> GetImportedDevices()
    {
        var result = new List<(int, string, string, string)>();
        string? sysfs = TryGetSysfsPath();
        if (sysfs == null) return result;
        foreach (var row in ReadStatus(sysfs))
        {
            if (row.Status == VdevStNull) continue;
            (string Host, int Service, string BusId)? location = ReadRecord(row.Port);
            if (location == null)
            {
                lock (s_lock)
                {
                    if (s_attached.TryGetValue(row.Port, out var own))
                        location = own;
                }
            }
            result.Add(location is { } l
                ? (row.Port, l.BusId, l.Service.ToString(CultureInfo.InvariantCulture), l.Host)
                : (row.Port, string.Empty, string.Empty, string.Empty));
        }
        return result;
    }

    /// <summary>The kernel side of the backend, for diagnostics.</summary>
    public static string? InstalledLayout() => IsAvailable() ? "Linux vhci-hcd" : null;

    private static void ClearNonBlocking(int fd)
    {
        try
        {
            int flags = fcntl(fd, FGetFl, 0);
            if (flags >= 0 && (flags & ONonBlock) != 0)
                fcntl(fd, FSetFl, flags & ~ONonBlock);
        }
        catch { /* not fatal: a blocking socket is the common case */ }
    }

    /// <summary>Write an attribute file. ASCII explicitly: a UTF-8 BOM (or
    /// any non-ASCII byte) in front of the "N ..." or the port number would
    /// make the kernel's parser reject the write.</summary>
    private static void WriteSysfs(string path, string text) =>
        File.WriteAllText(path, text, Encoding.ASCII);

    private static void SendAll(Socket socket, ReadOnlySpan<byte> data)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int n = socket.Send(data[sent..], SocketFlags.None);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            sent += n;
        }
    }

    private static void ReadExactly(Socket socket, Span<byte> dst)
    {
        int got = 0;
        while (got < dst.Length)
        {
            int n = socket.Receive(dst[got..], SocketFlags.None);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            got += n;
        }
    }

    private static void WriteFixedUtf8(Span<byte> dst, string value)
    {
        // Remainder is already zeroed by the caller.
        Encoding.UTF8.GetBytes(value.AsSpan(), dst[..(dst.Length - 1)]);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}

#endif
