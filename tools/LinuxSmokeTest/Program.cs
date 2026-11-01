using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using GKMD;

/// <summary>Manual Linux smoke test for the GKMD net10.0 build.
///
/// <para>It exercises the parts that only exist on Linux: the in-kernel
/// vhci_hcd attach (managed OP_REQ_IMPORT handshake + sysfs socket handoff),
/// enumeration by the kernel, usbhid binding, evdev input, and the
/// location-based detach. Run it as root through run-linux-smoke.sh.</para></summary>
internal static class Program
{
    private const string DefaultStatusPath = "/sys/devices/platform/vhci_hcd.0/status";

    // enum usbip_device_status: VDEV_ST_NULL=4, NOTASSIGNED=5, USED=6, ERROR=7.
    private const int VdevStNotAssigned = 5;
    private const int VdevStUsed = 6;

    private const ushort EvSyn = 0;
    private const ushort EvKey = 1;
    private const ushort EvAbs = 3;
    private const short PollIn = 1;

    private static int s_failures;

    private static int Main(string[] args)
    {
        string profileId = args.Length > 0 ? args[0] : "dualshock-4-v2";
        int seconds = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 6;

        Console.WriteLine("== GKMD Linux smoke test ==");
        Console.WriteLine($"kernel : {ReadText("/proc/sys/kernel/osrelease").Trim()}");
        Console.WriteLine($"euid   : {geteuid()}");
        Console.WriteLine($"backend available: {GKContext.IsUsbipBackendAvailable}");

        if (geteuid() != 0)
        {
            Console.Error.WriteLine("Run as root: the vhci-hcd sysfs attach file is root-writable.");
            return 2;
        }

        string statusPath;
        try { statusPath = FindStatusPath(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        Console.WriteLine($"status : {statusPath}");
        Console.WriteLine($"before : {HasImportedPort(statusPath)} imported port(s)");

        using var ctx = new GKContext();
        int loaded = ctx.LoadDefaultProfiles();
        Console.WriteLine($"profiles loaded: {loaded}");

        GKProfile profile;
        try
        {
            profile = ctx.GetProfile(profileId)
                ?? throw new InvalidOperationException($"profile '{profileId}' not found");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        Console.WriteLine($"profile: {profile.Id} / {profile.DisplayName} / " +
            $"{profile.VendorId:X4}:{profile.ProductId:X4}");

        GKController controller;
        try
        {
            controller = ctx.CreateController(profile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: CreateController threw: {ex}");
            DumpStatus(statusPath);
            DumpRecords();
            return 1;
        }

        Console.WriteLine("controller created");
        Check(WaitForImportedPort(statusPath, TimeSpan.FromSeconds(3)),
            "vhci-hcd reports an imported device");
        Check(WaitForUsedPort(statusPath, TimeSpan.FromSeconds(3)),
            "the import reached VDEV_ST_USED");
        DumpStatus(statusPath);
        DumpRecords();

        string? usbName = WaitForUsbDevice(profile, TimeSpan.FromSeconds(10));
        Check(usbName != null, $"kernel enumerated {profile.VendorId:X4}:{profile.ProductId:X4}");

        if (usbName != null)
        {
            Console.WriteLine($"usb device: {usbName}");
            // Interfaces are registered one by one during enumeration, so
            // wait for usbhid to claim the HID function (usually the last).
            var drivers = new List<string>();
            bool hidBound = false;
            var driverWait = Stopwatch.StartNew();
            while (driverWait.Elapsed < TimeSpan.FromSeconds(10))
            {
                drivers = InterfaceDrivers(usbName);
                hidBound = drivers.Exists(d => d.EndsWith("usbhid", StringComparison.Ordinal));
                if (hidBound) break;
                Thread.Sleep(100);
            }
            foreach (string line in drivers) Console.WriteLine($"  interface {line}");
            Check(hidBound, "usbhid bound the HID interface");
        }

        // Keep state (and therefore interrupt-IN reports) flowing while the
        // kernel's HID/input stack probes and while evdev is drained.
        using var stop = new CancellationTokenSource();
        Thread pump = StartStatePump(controller, profile, stop.Token);

        string? eventNode = usbName == null ? null
            : WaitForEventNode(profile, TimeSpan.FromSeconds(10));
        Check(eventNode != null, "the kernel created an evdev input node");

        if (eventNode != null)
        {
            Console.WriteLine($"event node: {eventNode}");
            EventStats stats = ReadEvents(eventNode, TimeSpan.FromSeconds(Math.Max(2, seconds)));
            Console.WriteLine($"evdev events: EV_ABS={stats.Abs}, EV_KEY(press)={stats.Key}, EV_SYN={stats.Syn}");
            Check(stats.Abs > 0, "axis reports reached evdev");
            Check(stats.Key > 0, "button reports reached evdev");
        }

        stop.Cancel();
        pump.Join(TimeSpan.FromSeconds(5));

        Console.WriteLine("detaching...");
        controller.Dispose();
        Check(WaitForNoImportedPort(statusPath, TimeSpan.FromSeconds(6)),
            "vhci-hcd freed every port after Dispose");
        DumpStatus(statusPath);
        DumpRecords();

        Console.WriteLine(s_failures == 0
            ? "RESULT: PASS"
            : $"RESULT: FAIL ({s_failures} check(s) failed)");
        return s_failures == 0 ? 0 : 1;
    }

    // ── vhci status ─────────────────────────────────────────────────────

    private static string FindStatusPath()
    {
        if (File.Exists(DefaultStatusPath)) return DefaultStatusPath;
        try
        {
            foreach (string dir in Directory.GetDirectories("/sys/devices/platform", "vhci_hcd*"))
            {
                string candidate = Path.Combine(dir, "status");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        throw new InvalidOperationException(
            "vhci-hcd status file not found. Load the module first: sudo modprobe vhci-hcd");
    }

    /// <summary>Rows are "hub port sta spd dev sockfd local_busid".</summary>
    private static IEnumerable<(string Hub, int Port, int Status)> StatusRows(string path)
    {
        string text = ReadText(path);
        foreach (string line in text.Split('\n'))
        {
            string[] p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 7) continue;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) continue;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int status)) continue;
            yield return (p[0], port, status);
        }
    }

    private static bool HasImportedPort(string path)
    {
        foreach (var row in StatusRows(path))
            if (row.Status >= VdevStNotAssigned) return true;
        return false;
    }

    private static bool HasUsedPort(string path)
    {
        foreach (var row in StatusRows(path))
            if (row.Status >= VdevStUsed) return true;
        return false;
    }

    private static bool WaitForUsedPort(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (HasUsedPort(path)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool WaitForImportedPort(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (HasImportedPort(path)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool WaitForNoImportedPort(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!HasImportedPort(path)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static void DumpStatus(string path)
    {
        Console.WriteLine("--- vhci status ---");
        Console.Write(ReadText(path));
        Console.WriteLine("-------------------");
    }

    private static void DumpRecords()
    {
        const string dir = "/var/run/vhci_hcd";
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir, "port*"))
                Console.WriteLine($"record {Path.GetFileName(file)}: {ReadText(file).Trim()}");
        }
        catch { }
    }

    // ── enumeration ─────────────────────────────────────────────────────

    private static string? WaitForUsbDevice(GKProfile profile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            string? found = FindUsbDevice(profile);
            if (found != null) return found;
            Thread.Sleep(200);
        }
        return null;
    }

    private static string? FindUsbDevice(GKProfile profile)
    {
        const string root = "/sys/bus/usb/devices";
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                // Device dirs are bus-port ("1-1", "1-1.2"); skip root hubs
                // ("usb1") and interfaces ("1-1:1.0").
                if (name.Contains(':') || !name.Contains('-')) continue;
                string vidText = ReadText(Path.Combine(dir, "idVendor")).Trim();
                string pidText = ReadText(Path.Combine(dir, "idProduct")).Trim();
                if (!ushort.TryParse(vidText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vid))
                    continue;
                if (!ushort.TryParse(pidText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort pid))
                    continue;
                if (vid == profile.VendorId && pid == profile.ProductId) return name;
            }
        }
        catch { }
        return null;
    }

    /// <summary>One "intf -> driver" line per interface of the device, with
    /// "(unbound)" for an interface no driver claimed.</summary>
    private static List<string> InterfaceDrivers(string usbName)
    {
        var result = new List<string>();
        const string root = "/sys/bus/usb/devices";
        try
        {
            foreach (string dir in Directory.GetDirectories(root, usbName + ":*"))
            {
                string iface = Path.GetFileName(dir);
                string driver;
                try
                {
                    string? target = new DirectoryInfo(Path.Combine(dir, "driver")).LinkTarget;
                    driver = string.IsNullOrEmpty(target)
                        ? "(unbound)"
                        : Path.GetFileName(target.TrimEnd('/'));
                }
                catch { driver = "(unbound)"; }
                result.Add($"{iface} -> {driver}");
            }
        }
        catch { }
        return result;
    }

    private static string? WaitForEventNode(GKProfile profile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            string? node = FindEventNode(profile);
            if (node != null) return node;
            Thread.Sleep(200);
        }
        return null;
    }

    private static string? FindEventNode(GKProfile profile)
    {
        try
        {
            string text = ReadText("/proc/bus/input/devices");
            foreach (string block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                Match id = Regex.Match(block, @"Vendor=([0-9a-fA-F]{4})\s+Product=([0-9a-fA-F]{4})");
                if (!id.Success) continue;
                ushort vid = ushort.Parse(id.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ushort pid = ushort.Parse(id.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (vid != profile.VendorId || pid != profile.ProductId) continue;
                Match handlers = Regex.Match(block, @"Handlers=.*\bevent(\d+)\b");
                if (handlers.Success) return "/dev/input/event" + handlers.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    // ── evdev ───────────────────────────────────────────────────────────

    private readonly record struct EventStats(int Abs, int Key, int Syn);

    private static EventStats ReadEvents(string path, TimeSpan duration)
    {
        int abs = 0, key = 0, syn = 0, printed = 0;
        byte[] record = new byte[24]; // input_event: timeval(16) + type/code/value
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 1, FileOptions.None);
        int fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        var fds = new[] { new PollFd { Fd = fd, Events = PollIn } };
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            fds[0].Revents = 0;
            int rc = poll(fds, 1, 200);
            if (rc < 0)
            {
                Console.Error.WriteLine($"poll failed: errno {Marshal.GetLastPInvokeError()}");
                break;
            }
            if (rc == 0 || (fds[0].Revents & PollIn) == 0) continue;

            int got = ReadExactly(stream, record);
            if (got != record.Length) break;

            ushort type = BitConverter.ToUInt16(record, 16);
            ushort code = BitConverter.ToUInt16(record, 18);
            int value = BitConverter.ToInt32(record, 20);
            switch (type)
            {
                case EvSyn:
                    syn++;
                    break;
                case EvAbs:
                    abs++;
                    if (printed++ < 10)
                        Console.WriteLine($"  EV_ABS code=0x{code:X2} value={value}");
                    break;
                case EvKey:
                    if (value == 1)
                    {
                        key++;
                        if (printed++ < 10)
                            Console.WriteLine($"  EV_KEY code=0x{code:X2} press");
                    }
                    break;
            }
        }
        return new EventStats(abs, key, syn);
    }

    private static int ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    // ── state pump ──────────────────────────────────────────────────────

    private static Thread StartStatePump(GKController controller, GKProfile profile, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            int i = 0;
            while (!token.IsCancellationRequested)
            {
                float phase = i++ * 0.15f;
                var state = new GKGamepadState
                {
                    Axes = GKGamepadStateHelpers.StandardAxes(profile,
                        leftStickX: 0.5f + 0.4f * MathF.Sin(phase),
                        leftStickY: 0.5f + 0.4f * MathF.Cos(phase),
                        leftTrigger: 0.5f + 0.5f * MathF.Sin(phase * 0.5f)),
                    // Toggle A so the event stream carries a key press too.
                    Buttons = (i / 8) % 2 == 0 ? GKButton.A : GKButton.None,
                };
                try { controller.SubmitState(state); } catch { }
                Thread.Sleep(8);
            }
        })
        { IsBackground = true, Name = "GkmdSmokeInput" };
        thread.Start();
        return thread;
    }

    // ── checks / plumbing ───────────────────────────────────────────────

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + what);
        if (!ok) s_failures++;
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int geteuid();
}
