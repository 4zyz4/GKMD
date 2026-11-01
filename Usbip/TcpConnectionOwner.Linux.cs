#if !GKMD_WINDOWS

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GKMD.Internal.Usbip;

/// <summary>Which user owns the other end of a loopback TCP connection,
/// from <c>/proc/net/tcp</c>.
///
/// <para>Linux has no kernel client holding the import: the importer is a
/// user-space program (the distribution's <c>usbip</c> tool, or GKMD
/// itself), and handing its socket to vhci-hcd through the sysfs
/// <c>attach</c> file requires root. The server therefore admits by uid —
/// root, or this process's own uid for the in-process probes — instead of
/// by the Windows System-process rule.</para></summary>
internal static partial class TcpConnectionOwner
{
    // /proc/net/tcp state 01 is TCP_ESTABLISHED.
    private const string Established = "01";

    private static int s_ownUid = int.MinValue;

    /// <summary>The uid that owns the client end of a connection this
    /// process accepted, or -1 when <c>/proc/net/tcp</c> holds no
    /// ESTABLISHED row for both ends (the client closed it, or the address
    /// is not IPv4) or the lookup fails. The server's accept loop calls it,
    /// so it never throws.</summary>
    public static int OfPeer(Socket accepted)
    {
        try
        {
            if (accepted.LocalEndPoint is not IPEndPoint local || accepted.RemoteEndPoint is not IPEndPoint remote
                || local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork)
                return -1;

            // The client's row has the two ends the other way round: its
            // local end is this socket's remote one.
            string clientLocal = Endpoint(remote);
            string clientRemote = Endpoint(local);

            foreach (string line in File.ReadLines("/proc/net/tcp"))
            {
                // sl local_address rem_address st tx_queue:rx_queue
                // tr:tm->when retrnsmt uid timeout inode ...
                string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 8) continue;
                if (!string.Equals(fields[3], Established, StringComparison.Ordinal)) continue;
                if (!string.Equals(fields[1], clientLocal, StringComparison.Ordinal)) continue;
                if (!string.Equals(fields[2], clientRemote, StringComparison.Ordinal)) continue;
                if (!int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid))
                    continue;
                return uid;
            }
            return -1;
        }
        catch { return -1; }
    }

    /// <summary>Whether this connection may import a device: a privileged
    /// (root) peer, which alone can hand its socket to vhci-hcd, or this
    /// process's own uid (the in-process probes and GKMD's own attach).
    /// Unlike Windows there is no opt-in flag to set: the uid rule covers
    /// the probes without widening anything for another program.</summary>
    public static bool IsAdmitted(Socket accepted)
    {
        int uid = OfPeer(accepted);
        return uid >= 0 && (uid == 0 || uid == OwnUid);
    }

    /// <summary>Effective uid of this process. Cached: this is on the
    /// accept path, and a process does not change identity.</summary>
    private static int OwnUid
    {
        get
        {
            int uid = s_ownUid;
            if (uid != int.MinValue) return uid;
            try { uid = geteuid(); }
            catch { uid = -2; } // unknown: root peers are still admitted
            s_ownUid = uid;
            return uid;
        }
    }

    /// <summary>The endpoint in <c>/proc/net/tcp</c>'s notation,
    /// <c>ADDR:PORT</c>: the IPv4 address as the host-order hex value of its
    /// network-order bytes (127.0.0.1 is <c>0100007F</c>) and the port as
    /// plain hex (<c>0x9512</c>).</summary>
    private static string Endpoint(IPEndPoint ep)
    {
        Span<byte> bytes = stackalloc byte[4];
        ep.Address.TryWriteBytes(bytes, out _);
        uint address = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return address.ToString("X8", CultureInfo.InvariantCulture)
            + ":" + ep.Port.ToString("X4", CultureInfo.InvariantCulture);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int geteuid();
}

#endif
