using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GKMD.Internal.Usbip;

/// <summary>The in-process USB/IP server (issue #39). One per process,
/// created lazily by <see cref="UsbipBackend"/>; listens on loopback and
/// exports each registered <see cref="UsbipEmulatedDevice"/> under its
/// busid. usbip-win2's vhci driver connects here (kernel WSK, one TCP
/// connection per attached device), performs the OP_REQ_IMPORT handshake,
/// and then streams CMD_SUBMIT / CMD_UNLINK, which this server answers
/// per the 0.9.8.0 wire contract read at source (see
/// <see cref="UsbipProtocol"/>).
///
/// <para>The listen port is a fixed range rather than ephemeral
/// (<see cref="BasePort"/>..+15, first free wins) so a later process can
/// name this server's host/service tuple when cleaning up after a crash:
/// the vhci driver starts background re-attach attempts when a connection
/// drops without a PLUGOUT (usbip-win2 device.cpp detach →
/// start_attach_attempts), and STOP_ATTACH_ATTEMPTS needs the exact
/// location to cancel them. The range is GKMD's own, clear of HIDMaestro's
/// 18509..18524 and 28509..28524 and FlexInput's 18008, so a sweep never
/// takes another program's server for a dead GKMD one. When every port in
/// the range is taken, the server takes an OS-assigned loopback port.</para></summary>
internal sealed class UsbipServer : IDisposable
{
    /// <summary>GKMD's own loopback range, clear of HIDMaestro's ranges and
    /// FlexInput's port. Unassigned, outside the IANA ephemeral range, and
    /// never 3240 so a real usbipd on its standard port is untouched.</summary>
    public const int BasePort = 37286;
    public const int PortRange = 16;

    /// <summary>How long a connection has to finish its handshake, the
    /// device list it asks for or the import reply it waits for, before the
    /// server closes it.</summary>
    internal const int HandshakeDeadlineMs = 5000;

    /// <summary>Connections in their handshake at once before the server
    /// closes new ones from user mode as they arrive.</summary>
    internal const int MaxHandshakes = 16;

    private static readonly object s_lock = new();
    private static UsbipServer? s_instance;

    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private readonly Dictionary<string, UsbipEmulatedDevice> _devices = new();
    private readonly List<Connection> _connections = new();
    private readonly object _registerGate = new();
    private int _handshakes;
    private volatile bool _stop;

    /// <summary>Test-only. The probes that play the host controller over
    /// loopback in process own their sockets in this process. Set, it
    /// admits a connection this process owns as it admits the host
    /// controller's. A program in another process cannot set it, and it
    /// admits no connection another process owns.</summary>
    internal static volatile bool AdmitImportsFromThisProcess;

    public int Port { get; }

    /// <summary>The process-wide server, started on first use.</summary>
    public static UsbipServer GetOrStart()
    {
        lock (s_lock)
        {
            if (s_instance != null && !s_instance._stop) return s_instance;
            s_instance = new UsbipServer();
            return s_instance;
        }
    }

    public static UsbipServer? Current { get { lock (s_lock) return s_instance; } }

    private UsbipServer()
    {
        Exception? last = null;
        TcpListener? listener = null;
        int port = 0;
        for (int i = 0; i < PortRange && listener == null; i++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, BasePort + i);
                l.Start();
                listener = l;
                port = BasePort + i;
            }
            catch (SocketException ex) { last = ex; }
        }
        if (listener == null)
        {
            // Every port in the range is taken: another program, which may
            // not be elevated, bound all of them first. Rather than fail
            // every composite persona, bind an OS-assigned loopback port
            // (port 0).
            try
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                listener = l;
                port = ((IPEndPoint)l.LocalEndpoint).Port;
            }
            catch (SocketException ex) { last = ex; }
        }
        _listener = listener ?? throw new InvalidOperationException(
            $"No free loopback port for the USB/IP server ({BasePort}..{BasePort + PortRange - 1} " +
            "and an OS-assigned port all failed).", last);
        Port = port;

        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "GKUsbipAccept",
        };
        _acceptThread.Start();
    }

    public void Register(UsbipEmulatedDevice device)
    {
        lock (_registerGate)
        lock (_devices) _devices[device.BusId] = device;
    }

    /// <summary>Run <paramref name="sweep"/> while <see cref="Register"/>
    /// waits. The stale sweep lists the imports, reads what the server
    /// exports, and then detaches each listed import whose bus id it does
    /// not export, by location. A device disposed after the listing frees
    /// its transport index, and a create on another thread can take that
    /// index, register the same bus id after the export read and attach
    /// before the sweep reaches the old row: the detach by location would
    /// then plug out the new import. No device registers until the sweep
    /// ends, so none is attached at a location the sweep is about to
    /// detach.</summary>
    internal void HoldingRegistrations(Action sweep)
    {
        lock (_registerGate) sweep();
    }

    public void Unregister(UsbipEmulatedDevice device)
    {
        // Identity-checked: a late dispose of a previous device must never
        // unregister a newer device that already claimed the same busid
        // (BulkId "1-N" is reused across mode switches).
        lock (_devices)
        {
            if (_devices.TryGetValue(device.BusId, out var current) && ReferenceEquals(current, device))
                _devices.Remove(device.BusId);
        }
    }

    private void AcceptLoop()
    {
        while (!_stop)
        {
            Socket client;
            try { client = _listener.AcceptSocket(); }
            catch { break; }
            client.NoDelay = true;
            // Any local program can connect. Each connection holds a thread
            // only until its handshake deadline, and past MaxHandshakes in
            // their handshake at once a new one is closed here, without a
            // thread, unless the host controller opened it. The driver
            // connects once per attach it runs, and runs them one at a time
            // (vhci_ioctl.cpp, the sequential default queue), so a flood
            // from user mode never turns an import away.
            if (Interlocked.Increment(ref _handshakes) > MaxHandshakes && !IsHostController(client))
            {
                Interlocked.Decrement(ref _handshakes);
                try { client.Close(); } catch { }
                continue;
            }
            var conn = new Connection(this, client);
            lock (_connections) _connections.Add(conn);
            var t = new Thread(conn.Run) { IsBackground = true, Name = "GKUsbipConn" };
            t.Start();
        }
    }

    /// <summary>Whether the host controller opened this connection: its
    /// client end is owned by the System process. Only that connection may
    /// import a device, since an import carries the device's input, a
    /// keyboard persona's typed keys included, and an import from anywhere
    /// else would also turn the host controller's own away ST_DEV_BUSY.
    /// </summary>
    private static bool IsHostController(Socket client)
    {
        int pid = TcpConnectionOwner.OfPeer(client);
        return pid == TcpConnectionOwner.SystemProcessId
            || (AdmitImportsFromThisProcess && pid == Environment.ProcessId);
    }

    private UsbipEmulatedDevice? Find(string busid)
    {
        lock (_devices) return _devices.TryGetValue(busid, out var d) ? d : null;
    }

    /// <summary>Live emulated devices, for the eviction path in
    /// <see cref="UsbipBackend.DetachAllOwned"/> (issue #44).</summary>
    internal List<UsbipEmulatedDevice> SnapshotDevices() => Snapshot();

    private List<UsbipEmulatedDevice> Snapshot()
    {
        lock (_devices) return new List<UsbipEmulatedDevice>(_devices.Values);
    }

    internal void OnConnectionClosed(Connection c)
    {
        lock (_connections) _connections.Remove(c);
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
        List<Connection> conns;
        lock (_connections) conns = new List<Connection>(_connections);
        foreach (var c in conns) c.Close();
        lock (s_lock) if (s_instance == this) s_instance = null;
    }

    /// <summary>One accepted TCP connection: the import handshake, then
    /// the command loop for the imported device. All sends serialize on
    /// <see cref="_sendLock"/> because the audio pacing thread, the input
    /// pump, and the reader thread all complete URBs.</summary>
    internal sealed class Connection
    {
        private readonly UsbipServer _server;
        private readonly Socket _socket;
        private readonly object _sendLock = new();
        private UsbipEmulatedDevice? _device;

        // The handshake's deadline. The timer closes the socket only while
        // the connection is still in its handshake, and whichever of the
        // timer and the handshake moves the phase first decides.
        private const int InHandshake = 0, Answered = 1, Expired = 2;
        private int _phase;
        private readonly Timer _deadline;
        // 1 while the connection counts toward the server's handshakes.
        private int _counted = 1;

        public Connection(UsbipServer server, Socket socket)
        {
            _server = server;
            _socket = socket;
            _deadline = new Timer(static s => ((Connection)s!).Expire(), this, HandshakeDeadlineMs, Timeout.Infinite);
        }

        private void Expire()
        {
            if (Interlocked.CompareExchange(ref _phase, Expired, InHandshake) == InHandshake) Close();
        }

        /// <summary>End the handshake: the deadline no longer applies. False
        /// when the deadline came first and closed the socket.</summary>
        private bool LeaveHandshake()
        {
            bool left = Interlocked.CompareExchange(ref _phase, Answered, InHandshake) == InHandshake;
            _deadline.Dispose();
            Uncount();
            return left;
        }

        private void Uncount()
        {
            if (Interlocked.Exchange(ref _counted, 0) == 1) Interlocked.Decrement(ref _server._handshakes);
        }

        public void Close()
        {
            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            try { _socket.Close(); } catch { }
        }

        public void Run()
        {
            try
            {
                Span<byte> op = stackalloc byte[8];
                ReadExactly(op);
                ushort version = (ushort)((op[0] << 8) | op[1]);
                ushort code = (ushort)((op[2] << 8) | op[3]);
                if (version != UsbipProtocol.Version) return;

                if (code == UsbipProtocol.OpReqDevlist)
                {
                    SendDevlist();
                    return;
                }
                if (code != UsbipProtocol.OpReqImport) return;

                Span<byte> busidBuf = stackalloc byte[UsbipProtocol.BusIdSize];
                ReadExactly(busidBuf);
                string busid = UsbipProtocol.ReadFixedUtf8(busidBuf);

                Span<byte> reply = stackalloc byte[8 + UsbipProtocol.UsbipUsbDeviceSize];
                if (!IsHostController(_socket))
                {
                    // ST_NA, before the bus id is looked up, so the answer
                    // says nothing about what this server exports.
                    UsbipProtocol.WriteOpCommon(reply, UsbipProtocol.OpRepImport, UsbipProtocol.StNa);
                    Send(reply[..8]);
                    return;
                }

                var device = _server.Find(busid);
                if (device == null)
                {
                    UsbipProtocol.WriteOpCommon(reply, UsbipProtocol.OpRepImport, UsbipProtocol.StNodev);
                    Send(reply[..8]);
                    // The wait for the controller's close has its own bound.
                    LeaveHandshake();
                    CancelAttempt(busid);
                    return;
                }
                if (!device.TryClaimConnection(this))
                {
                    // Already imported on another connection: ST_DEV_BUSY,
                    // the same answer a busy exported device gives
                    // (consts.h op_status_t).
                    UsbipProtocol.WriteOpCommon(reply, UsbipProtocol.OpRepImport, 2);
                    Send(reply[..8]);
                    return;
                }

                // Held from the claim on, so the finally releases the claim
                // on every path out, a reply that fails to send included:
                // a claim left on a dead connection answers every later
                // import of the bus id ST_DEV_BUSY.
                _device = device;

                UsbipProtocol.WriteOpCommon(reply, UsbipProtocol.OpRepImport, UsbipProtocol.StOk);
                WriteDeviceBlock(reply[8..], device);
                Send(reply);
                if (!LeaveHandshake()) return;

                CommandLoop(device);
            }
            catch
            {
                // Socket torn down; the vhci side detaches on its own.
            }
            finally
            {
                _deadline.Dispose();
                Uncount();
                _device?.DetachConnection(this);
                _server.OnConnectionClosed(this);
                Close();
            }
        }

        /// <summary>An import of a bus id this server does not export is a
        /// re-attach attempt the host controller keeps for a server that
        /// held this port before, or for a device this server has since
        /// removed. Answered ST_NODEV it tries again, for about two hours
        /// (persistent.cpp can_reattach, on_plugin_hardware). It is canceled
        /// by its exact location once the controller closes this
        /// connection: the controller completes the attempt, which re-arms
        /// its timer, before it closes the socket (vhci_ioctl.cpp complete,
        /// workitem_cleanup).</summary>
        private void CancelAttempt(string busid)
        {
            try
            {
                _socket.ReceiveTimeout = 5000;
                Span<byte> one = stackalloc byte[1];
                _socket.Receive(one, SocketFlags.None);
            }
            catch { /* closed, reset or timed out: cancel all the same */ }
            VhciClient.StopAttachAttempts("127.0.0.1", _server.Port, busid);
        }

        private void WriteDeviceBlock(Span<byte> b, UsbipEmulatedDevice device)
        {
            var dd = device.Descriptors.DeviceDescriptor;
            UsbipProtocol.WriteUsbipUsbDevice(b,
                path: $"/hidmaestro/{device.BusId}",
                busid: device.BusId,
                busnum: device.Devid >> 16,
                devnum: device.Devid & 0xFFFF,
                speed: device.Descriptors.Speed,
                vid: device.Descriptors.VendorId,
                pid: device.Descriptors.ProductId,
                bcdDevice: device.Descriptors.BcdDevice,
                devClass: dd[4], devSubClass: dd[5], devProtocol: dd[6],
                configurationValue: device.Descriptors.ConfigurationValue,
                numConfigurations: dd[17],
                numInterfaces: device.Descriptors.NumInterfaces);
        }

        private void SendDevlist()
        {
            var devices = _server.Snapshot();
            int size = 8 + 4;
            foreach (var d in devices)
                size += UsbipProtocol.UsbipUsbDeviceSize + d.Descriptors.NumInterfaces * 4;
            var buf = new byte[size];
            UsbipProtocol.WriteOpCommon(buf, UsbipProtocol.OpRepDevlist, UsbipProtocol.StOk);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), (uint)devices.Count);
            int off = 12;
            foreach (var d in devices)
            {
                WriteDeviceBlock(buf.AsSpan(off), d);
                off += UsbipProtocol.UsbipUsbDeviceSize;
                // One usbip_usb_interface (class, subclass, protocol, pad)
                // per interface, from the configuration blob's alt-0
                // interface descriptors in order.
                var blob = d.Descriptors.ConfigurationDescriptor;
                for (int o = 0; o + 2 <= blob.Length; o += blob[o])
                {
                    if (blob[o] < 2) break;
                    if (blob[o + 1] == 0x04 && blob[o + 3] == 0) // interface, alt 0
                    {
                        buf[off] = blob[o + 5];
                        buf[off + 1] = blob[o + 6];
                        buf[off + 2] = blob[o + 7];
                        off += 4;
                    }
                }
            }
            Send(buf);
        }

        // The reader's buffers for a submit's OUT data and isochronous
        // descriptors when the two do not fit the receive buffer together,
        // and for its isochronous packets, grown to the host's sizes and
        // reused, so a transfer allocates nothing. Every use of them ends
        // inside HandleSubmit.
        private byte[] _outData = Array.Empty<byte>();
        private byte[] _isoDescs = Array.Empty<byte>();
        private (uint Offset, uint Length)[] _isoPackets = Array.Empty<(uint, uint)>();

        // The command loop's receive buffer. Each Receive takes what the
        // socket holds, up to the buffer's size, so a command's header, its
        // OUT data and its isochronous descriptors that arrived together
        // cost one call, and so do commands the host controller sent back
        // to back. Bytes _rxStart to _rxEnd are received and not yet read.
        // Only the connection's reader touches it. A command whose OUT data
        // and descriptors together fit in the buffer is read in place.
        // Longer commands go to the arrays above.
        private const int RxBufferSize = 4096;
        private byte[] _rx = Array.Empty<byte>();
        private int _rxStart, _rxEnd;

        /// <summary>The next <paramref name="n"/> bytes of the stream, at
        /// most <see cref="RxBufferSize"/>, read in place. The span stays
        /// valid until the next call. A longer read goes through
        /// <see cref="ReadInto"/>.</summary>
        private ReadOnlySpan<byte> Take(int n)
        {
            if (_rxEnd - _rxStart < n) Fill(n);
            var taken = new ReadOnlySpan<byte>(_rx, _rxStart, n);
            _rxStart += n;
            if (_rxStart == _rxEnd) _rxStart = _rxEnd = 0;
            return taken;
        }

        /// <summary>Receive until the buffer holds <paramref name="n"/>
        /// bytes from <see cref="_rxStart"/>. Kept out of <see cref="Take"/>,
        /// which often returns without a call to the socket: a method the
        /// JIT inlines a native call into sets up the call's frame on every
        /// entry.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void Fill(int n)
        {
            // A longer read would ask the socket for bytes with no room left
            // to put them.
            if (n > _rx.Length) throw new InvalidOperationException($"a read of {n} bytes past the {_rx.Length}-byte receive buffer");
            if (_rxStart + n > _rx.Length)
            {
                // What is left moves to the front, so the rest of the
                // command lands after it.
                int left = _rxEnd - _rxStart;
                _rx.AsSpan(_rxStart, left).CopyTo(_rx);
                _rxStart = 0;
                _rxEnd = left;
            }
            while (_rxEnd - _rxStart < n)
            {
                int got = _socket.Receive(_rx.AsSpan(_rxEnd), SocketFlags.None);
                if (got <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                _rxEnd += got;
            }
        }

        /// <summary>The next <paramref name="dst"/>.Length bytes of the
        /// stream into <paramref name="dst"/>, of any length: what the receive
        /// buffer holds, then the rest straight from the socket, which runs
        /// only once the buffer is empty.</summary>
        private void ReadInto(Span<byte> dst)
        {
            int held = Math.Min(_rxEnd - _rxStart, dst.Length);
            Take(held).CopyTo(dst);
            ReadExactly(dst[held..]);
        }

        private void CommandLoop(UsbipEmulatedDevice device)
        {
            _rx = new byte[RxBufferSize];
            while (true)
            {
                var cmd = UsbipProtocol.ParseHeader(Take(UsbipProtocol.HeaderSize));
                // A device disposed under an open connection answers nothing
                // more. Ending the connection lets usbip-win2 detach it, and
                // the re-attach that follows finds no device.
                if (device.IsDisposed) return;

                if (cmd.Command == UsbipProtocol.CmdSubmit)
                {
                    ReadOnlySpan<byte> payload = default;
                    Span<(uint Offset, uint Length)> iso = default;

                    bool isIsoch = cmd.NumberOfPackets != UsbipProtocol.NumberOfPacketsNonIsoch;
                    if (isIsoch && (cmd.NumberOfPackets < 0 || cmd.NumberOfPackets > UsbipProtocol.MaxIsoPackets))
                        throw new InvalidOperationException($"number_of_packets {cmd.NumberOfPackets} out of range");

                    int dataLength = !cmd.IsIn && cmd.TransferBufferLength > 0 ? cmd.TransferBufferLength : 0;
                    int descLength = isIsoch ? cmd.NumberOfPackets * UsbipProtocol.IsoDescriptorSize : 0;
                    ReadOnlySpan<byte> descs = default;
                    if ((long)dataLength + descLength <= RxBufferSize)
                    {
                        // Both in place, from one take, so neither moves
                        // while the other is read.
                        var rest = Take(dataLength + descLength);
                        payload = rest[..dataLength];
                        descs = rest[dataLength..];
                    }
                    else
                    {
                        // Longer than the buffer together, which an
                        // isochronous URB of up to 1024 packets can be on
                        // its descriptors alone: each into its own array.
                        if (_outData.Length < dataLength) _outData = new byte[dataLength];
                        if (_isoDescs.Length < descLength) _isoDescs = new byte[descLength];
                        var data = _outData.AsSpan(0, dataLength);
                        var desc = _isoDescs.AsSpan(0, descLength);
                        ReadInto(data);
                        ReadInto(desc);
                        payload = data;
                        descs = desc;
                    }
                    if (isIsoch)
                    {
                        int n = cmd.NumberOfPackets;
                        if (_isoPackets.Length < n) _isoPackets = new (uint, uint)[n];
                        iso = _isoPackets.AsSpan(0, n);
                        for (int i = 0; i < n; i++)
                            iso[i] = UsbipProtocol.ReadIsoDescriptor(descs[(i * UsbipProtocol.IsoDescriptorSize)..]);
                    }

                    device.HandleSubmit(in cmd, payload, iso);
                }
                else if (cmd.Command == UsbipProtocol.CmdUnlink)
                {
                    device.HandleUnlink(cmd.Seqnum, cmd.UnlinkSeqnum);
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected command {cmd.Command}");
                }
            }
        }

        // ── Reply builders ───────────────────────────────────────────────

        public void SendRetSubmitNonIso(uint seqnum, int status, int actualLength, byte[]? inPayload)
            => SendRetSubmitNonIso(seqnum, status, actualLength, (ReadOnlySpan<byte>)inPayload);

        /// <summary>The same reply from a span, so a caller that fills a
        /// device-owned buffer sends it without copying it to an array of
        /// its own.</summary>
        public void SendRetSubmitNonIso(uint seqnum, int status, int actualLength, ReadOnlySpan<byte> inPayload)
        {
            int total = UsbipProtocol.HeaderSize + inPayload.Length;
            lock (_sendLock)
            {
                var buf = TxBuffer(total);
                UsbipProtocol.WriteRetSubmit(buf, seqnum, status, actualLength, 0,
                    UsbipProtocol.NumberOfPacketsNonIsoch, 0);
                inPayload.CopyTo(buf.AsSpan(UsbipProtocol.HeaderSize));
                SendLocked(buf.AsSpan(0, total));
            }
        }

        // The reply being sent, built under _sendLock in a buffer the
        // connection keeps: grown to the longest reply and reused, so a reply
        // allocates nothing.
        private byte[] _tx = new byte[UsbipProtocol.HeaderSize + 4096];

        /// <summary>The send buffer, at least <paramref name="length"/> bytes
        /// long. Caller holds <see cref="_sendLock"/>.</summary>
        private byte[] TxBuffer(int length)
        {
            if (_tx.Length < length) _tx = new byte[length];
            return _tx;
        }

        /// <summary>Isochronous RET_SUBMIT. IN: compacted data then the
        /// descriptors, offsets echoing the submit, actual_length the sum
        /// of per-packet actuals. OUT: descriptors only, actual_length
        /// echoing the transfer buffer length. Wire rules from usbip-win2
        /// 0.9.8.0 wsk_receive.cpp fill_isoc_data (zero-copy reuses
        /// wsk_receive_irp.cpp prepare_wsk_mdl).</summary>
        public void SendRetSubmitIso(UsbAudioEngine.PendingIso p, ReadOnlySpan<byte> inCompacted, int perPacketActual)
        {
            int n = p.PacketCount;
            int dataLen = p.IsIn ? inCompacted.Length : 0;
            int total = UsbipProtocol.HeaderSize + dataLen + n * UsbipProtocol.IsoDescriptorSize;
            lock (_sendLock)
            {
                var buf = TxBuffer(total);
                int actual = p.IsIn ? perPacketActual * n : p.TransferBufferLength;
                UsbipProtocol.WriteRetSubmit(buf, p.Seqnum, 0, actual, p.FrameAtCompletion, n, 0);
                if (dataLen > 0) inCompacted.CopyTo(buf.AsSpan(UsbipProtocol.HeaderSize));
                int d = UsbipProtocol.HeaderSize + dataLen;
                for (int i = 0; i < n; i++)
                {
                    UsbipProtocol.WriteIsoDescriptor(buf.AsSpan(d + i * UsbipProtocol.IsoDescriptorSize),
                        offset: p.Packets[i].Offset,
                        length: p.Packets[i].Length,
                        actualLength: p.IsIn ? (uint)perPacketActual : p.Packets[i].Length,
                        status: 0);
                }
                SendLocked(buf.AsSpan(0, total));
            }
        }

        public void SendRetUnlink(uint seqnum, int status)
        {
            Span<byte> buf = stackalloc byte[UsbipProtocol.HeaderSize];
            UsbipProtocol.WriteRetUnlink(buf, seqnum, status);
            Send(buf);
        }

        private void Send(ReadOnlySpan<byte> data)
        {
            lock (_sendLock) SendLocked(data);
        }

        /// <summary>Caller holds <see cref="_sendLock"/>.</summary>
        private void SendLocked(ReadOnlySpan<byte> data)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                int n = _socket.Send(data[sent..], SocketFlags.None);
                if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                sent += n;
            }
        }

        private void ReadExactly(Span<byte> dst)
        {
            int got = 0;
            while (got < dst.Length)
            {
                int n = _socket.Receive(dst[got..], SocketFlags.None);
                if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                got += n;
            }
        }
    }
}
