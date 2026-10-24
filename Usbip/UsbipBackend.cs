using System;
using System.Threading;

namespace GKMD.Internal.Usbip;

/// <summary>Orchestrates one usbip-backend controller's lifecycle
/// (issue #39): deploy the bundled transport if this machine does not
/// have it yet, start the in-process server, register the emulated
/// device, sweep any stale state a crashed prior process left in the
/// vhci driver, then attach and hand back a handle whose disposal
/// detaches and tears everything down in order.
///
/// <para>Composite personas just work. The usbip-win2 transport ships
/// inside GKMD.Core.dll and installs itself on the first create:
/// no separate package, no second download, nothing for a user to go
/// find. See <see cref="UsbipDriverInstaller"/>.</para></summary>
internal static class UsbipBackend
{
    /// <summary>True when the transport is already installed and a
    /// composite create needs no install step. Creating a composite
    /// controller does NOT require this to be true: it installs on
    /// demand. Consumers use this only to decide whether to warn about
    /// the one-time install.</summary>
    public static bool IsAvailable => VhciClient.IsAvailable();

    private static int s_staleSweepDone;

    public static UsbipBackendHandle CreateDevice(ControllerProfile profile, int index,
                                                  Action<string>? progress = null)
    {
        // Deploy on demand. Idempotent, and a no-op on every machine that
        // already has it.
        UsbipDriverInstaller.EnsureInstalled(progress);

        var server = UsbipServer.GetOrStart();
        SweepStaleOnce(server);

        var device = new UsbipEmulatedDevice(profile, index);
        server.Register(device);
        try
        {
            // Low-latency (WSK event callbacks) is the default receive mode;
            // pass UsbipReceiveMode.ZeroCopy to fall back to the pre-0.9.8.0
            // MDL path if a persona's isochronous audio misbehaves.
            int vhciPort = VhciClient.Attach("127.0.0.1", server.Port, device.BusId,
                                             UsbipReceiveMode.LowLatency);
            return new UsbipBackendHandle(server, device, vhciPort);
        }
        catch
        {
            // The device was exported from Register on, so a re-attach
            // attempt the host controller still runs for this location can
            // have imported it ahead of this attach, which then failed
            // ST_DEV_BUSY. Remove that import with the device, the way the
            // handle's Dispose does, or it stays plugged in on a device
            // that no longer answers.
            try { VhciClient.DetachLocation("127.0.0.1", server.Port, device.BusId); } catch { }
            VhciClient.StopAttachAttempts("127.0.0.1", server.Port, device.BusId);
            server.Unregister(device);
            device.Dispose();
            throw;
        }
    }

    /// <summary>Once per process: plug out imports whose server is gone and
    /// cancel their re-attach attempts. The vhci driver starts re-attach
    /// attempts whenever a connection drops without a plugout (usbip-win2
    /// device.cpp detach path), and those would spin against dead ports
    /// forever. An import on this process's own port that none of its
    /// controllers made was left by the program that had the port before.
    /// Another running program's imports, on a port its server still
    /// listens on, are never touched.</summary>
    private static void SweepStaleOnce(UsbipServer server)
    {
        if (Interlocked.Exchange(ref s_staleSweepDone, 1) != 0) return;
        SweepStale(server);
    }

    /// <summary>The sweep <see cref="SweepStaleOnce"/> runs. Creates on
    /// other threads pass the once-flag at once, so one can register and
    /// attach while this runs. Registrations wait for the whole sweep
    /// (<see cref="UsbipServer.HoldingRegistrations"/>), so a new device
    /// that takes a gone device's bus id is never attached at a location
    /// the sweep then detaches.</summary>
    internal static void SweepStale(UsbipServer server)
    {
        try
        {
            server.HoldingRegistrations(() =>
            {
                var rows = VhciClient.GetImportedDevices();
                var own = new System.Collections.Generic.HashSet<string>();
                foreach (var device in server.SnapshotDevices()) own.Add(device.BusId);
                var listening = ListeningLoopbackPorts();
                foreach (var row in rows)
                {
                    if (row.Host != "127.0.0.1" || !TryServicePort(row.Service, out int port)) continue;
                    bool ownPort = port == server.Port;
                    if (!ownPort && !IsOurPort(port)) continue;
                    if (ownPort)
                    {
                        // This process's own server exports these; anything
                        // else on its port was left by the program that held
                        // the port before.
                        if (!own.Contains(row.BusId))
                            VhciClient.DetachLocation(row.Host, port, row.BusId);
                    }
                    else if (!listening.Contains(port))
                    {
                        // Nothing listens on the port: its server is gone.
                        VhciClient.DetachLocation(row.Host, port, row.BusId);
                    }
                }
                CancelAttemptsForDeadPorts(server.Port, own, listening);
            });
        }
        catch { /* best-effort recovery */ }
    }

    /// <summary>Cancel re-attach attempts for servers that are gone. A port
    /// with a live listener may be another GKMD program's server, and
    /// canceling its attempts would cancel that program's own controller
    /// coming back. On this process's own port, only the bus ids it does
    /// not export are canceled. Servers before the second port range
    /// existed only bound the first range, so its legacy bus ids are tried
    /// there alone; the whole list goes in one request per handle.</summary>
    private static void CancelAttemptsForDeadPorts(int ownPort,
        System.Collections.Generic.HashSet<string> own, System.Collections.Generic.HashSet<int> listening)
    {
        var locations = new System.Collections.Generic.List<(int Port, string BusId)>();
        for (int port = UsbipServer.BasePort; port < UsbipServer.BasePort + UsbipServer.PortRange; port++)
        {
            if (port == ownPort || listening.Contains(port)) continue;
            for (int devnum = 1; devnum <= 8; devnum++)
                locations.Add((port, $"1-{devnum}"));
        }
        for (int devnum = 1; devnum <= 8; devnum++)
        {
            string busId = $"1-{devnum}";
            if (!own.Contains(busId)) locations.Add((ownPort, busId));
        }
        VhciClient.StopAttachAttempts("127.0.0.1", locations);
    }

    /// <summary>Loopback TCP ports something listens on.</summary>
    private static System.Collections.Generic.HashSet<int> ListeningLoopbackPorts()
    {
        var set = new System.Collections.Generic.HashSet<int>();
        foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (System.Net.IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(System.Net.IPAddress.Any))
                set.Add(ep.Port);
        return set;
    }

    /// <summary>Detach every usbip device this SDK owns, including the
    /// calling session's own (issue #44).
    ///
    /// <para>A composite persona is a USB device behind the emulated host
    /// controller, and by design it carries no GKMD token anywhere
    /// on its own devnode (#42). The device sweep in
    /// <c>DeviceOrchestrator.RemoveAllVirtualControllers</c> walks the ROOT
    /// and SWD enumerators looking for exactly that token, so it can never
    /// see a composite: an SDK consumer that created one and exited left
    /// the device enumerated behind it.</para>
    ///
    /// <para>Distinct from <see cref="SweepStaleOnce"/>, which runs once per
    /// process at create time and deliberately SKIPS the current session's
    /// port so it cannot unplug the device it is about to use. Eviction
    /// wants the opposite, so this takes no such exclusion and is not
    /// gated to a single call. Best-effort: a machine without the transport
    /// installed has nothing to detach and must not throw.</para></summary>
    public static void DetachAllOwned()
    {
        try
        {
            // Stop this process's emulated devices FIRST. Their input pump
            // threads map the shared input section directly and read it in a
            // loop rather than draining a stop event. The sweep destroys
            // those sections moments later, so leaving a pump running here is
            // not a leak, it is an access violation on a background thread
            // that kills the whole process. Disposing joins the pumps before
            // anything is unmapped.
            var server = UsbipServer.Current;
            if (server != null)
            {
                foreach (var device in server.SnapshotDevices())
                {
                    try { server.Unregister(device); device.Dispose(); } catch { }
                }
            }

            if (!VhciClient.IsAvailable()) return;
            int ownPort = UsbipServer.Current?.Port ?? 0;
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (row.Host != "127.0.0.1" || !TryServicePort(row.Service, out int port)) continue;
                if (port != ownPort && !IsOurPort(port)) continue;
                // By location: the vhci port the import sits on may have
                // changed since the attach, and by now it may hold another
                // program's device.
                VhciClient.DetachLocation(row.Host, port, row.BusId);
            }
            var ports = new System.Collections.Generic.List<int>();
            for (int first = UsbipServer.BasePort; first < UsbipServer.BasePort + UsbipServer.PortRange; first++)
                ports.Add(first);
            for (int first = UsbipServer.SecondBasePort; first < UsbipServer.SecondBasePort + UsbipServer.PortRange; first++)
                ports.Add(first);
            if (ownPort > 0 && !ports.Contains(ownPort)) ports.Add(ownPort);
            var locations = new System.Collections.Generic.List<(int Port, string BusId)>();
            foreach (int port in ports)
                for (int devnum = 1; devnum <= 8; devnum++)
                    locations.Add((port, $"1-{devnum}"));
            VhciClient.StopAttachAttempts("127.0.0.1", locations);
        }
        catch { /* best-effort: eviction must never throw out of a sweep */ }
    }

    private static bool TryServicePort(string service, out int port) => int.TryParse(service, out port);

    /// <summary>A port of one of the SDK's two ranges
    /// (<see cref="UsbipServer"/>).</summary>
    private static bool IsOurPort(int port) =>
        (port >= UsbipServer.BasePort && port < UsbipServer.BasePort + UsbipServer.PortRange)
        || (port >= UsbipServer.SecondBasePort && port < UsbipServer.SecondBasePort + UsbipServer.PortRange);
}

/// <summary>The live backing of one usbip-backend controller. Disposal
/// order: PLUGOUT first, which makes the driver close the socket before
/// unplugging the UDE device (usbip-win2 device.cpp detach runs
/// close_socket ahead of plugout_and_delete), so the reader thread sees
/// EOF and runs the detach path; then unregister; then tear the device
/// down, which joins its pump threads before the shared sections go
/// away.</summary>
internal sealed class UsbipBackendHandle : IDisposable
{
    private readonly UsbipServer _server;
    public UsbipEmulatedDevice Device { get; }
    public int VhciPort { get; }
    private int _disposed;

    internal UsbipBackendHandle(UsbipServer server, UsbipEmulatedDevice device, int vhciPort)
    {
        _server = server;
        Device = device;
        VhciPort = vhciPort;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // By location: after a dropped connection the driver plugs the
        // device in again on a new port, and the port it had may hold
        // another program's device.
        try { VhciClient.DetachLocation("127.0.0.1", _server.Port, Device.BusId); } catch { }

        // PLUGOUT is synchronous but Windows' removal of the emulated
        // device's child functions (audio, HID) is not. Wait for the
        // location to actually leave the imported list before letting it be
        // reused; otherwise a fast mode switch re-attaches onto a port whose
        // old audio devnode is still alive and Windows keeps the audio
        // function on the new (audio-less) persona.
        if (!VhciClient.WaitForLocationDetached("127.0.0.1", _server.Port, Device.BusId, TimeoutScale.Apply(3_000)))
        {
            // One retry: the first plugout may have raced a pending PnP
            // operation. Best-effort, bounded, never throws.
            try { VhciClient.DetachLocation("127.0.0.1", _server.Port, Device.BusId); } catch { }
            VhciClient.WaitForLocationDetached("127.0.0.1", _server.Port, Device.BusId, TimeoutScale.Apply(2_000));
        }

        // An import that dropped while the controller lived left an attempt
        // for this location, and nothing names it once the device's entry
        // goes.
        VhciClient.StopAttachAttempts("127.0.0.1", _server.Port, Device.BusId);
        _server.Unregister(Device);
        Device.Dispose();
    }
}
