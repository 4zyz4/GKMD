using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GKMD.Internal.Usbip;

/// <summary>Microsoft Gaming Input Protocol (GIP) wire constants and the
/// header codec shared by the Xbox One persona's device-side responder.
///
/// <para>Every GIP message is <c>[command][options][sequence]</c> followed by
/// a LEB128 variable-length <c>packet_length</c> (and, when the chunk option
/// is set, a LEB128 <c>chunk_offset</c>), then the payload. The sender pads
/// an odd-length header to an even one by setting the high bit of the last
/// length byte and appending a zero; decoders read the zero as a continuation
/// byte whose seven bits are all zero, so the decoded length is unchanged.
/// See [MS-GIPUSB] and xone's <c>bus/protocol.c</c>.</para></summary>
internal static class GipProtocol
{
    // Core (system) commands.
    public const byte CmdAcknowledge = 0x01;
    public const byte CmdAnnounce = 0x02;
    public const byte CmdStatus = 0x03;
    public const byte CmdIdentify = 0x04;
    public const byte CmdPower = 0x05;
    public const byte CmdAuthenticate = 0x06;
    public const byte CmdVirtualKey = 0x07;
    public const byte CmdAudioControl = 0x08;
    public const byte CmdLed = 0x0a;
    public const byte CmdFirmware = 0x0c;
    public const byte CmdSerialNumber = 0x1e;

    // Client commands.
    public const byte CmdRumble = 0x09;
    public const byte CmdInput = 0x20;

    // Options.
    public const byte OptAck = 0x10;
    public const byte OptInternal = 0x20;
    public const byte OptChunkStart = 0x40;
    public const byte OptChunk = 0x80;

    // Attachment/client id lives in the low four option bits; we are id 0.
    public const byte ClientIdMask = 0x0F;

    public const byte VkLeftWin = 0x5B; // the Guide button's virtual key

    /// <summary>Bit 7 set = connected; bits 2-3 battery type; bits 0-1
    /// battery level.</summary>
    public const byte StatusConnected = 0x80;

    /// <summary>Encodes one GIP frame (header + payload), padding the header
    /// to an even length exactly as xone's <c>gip_encode_header</c> does.</summary>
    public const int PktMaxLength = 58;

    public static byte[] EncodeFrame(byte command, byte options, byte sequence,
                                     ReadOnlySpan<byte> payload)
    {
        Span<byte> lenBytes = stackalloc byte[5];
        int lenLen = EncodeVarint((uint)payload.Length, lenBytes);
        int headerLen = 3 + lenLen;
        bool pad = (headerLen & 1) != 0;

        var buf = new byte[headerLen + (pad ? 1 : 0) + payload.Length];
        buf[0] = command;
        buf[1] = options;
        buf[2] = sequence;
        lenBytes.Slice(0, lenLen).CopyTo(buf.AsSpan(3));

        int p = headerLen;
        if (pad)
        {
            buf[p - 1] |= 0x80;
            buf[p] = 0x00;
            p++;
        }
        payload.CopyTo(buf.AsSpan(p));
        return buf;
    }

    /// <summary>Encodes a message, chunking it when the payload exceeds
    /// <see cref="PktMaxLength"/> so every frame fits the host's 64-byte
    /// interrupt-IN transfer. The final frame is a zero-length completion
    /// chunk; the receiver only dispatches the reassembled message when it
    /// sees that frame (xone's <c>gip_process_pkt_chunked</c>). Chunk 0's
    /// chunk-offset field carries the total length, later chunks their own
    /// offset, matching <c>gip_send_remaining_chunks</c>.</summary>
    public static List<byte[]> EncodeMessage(byte command, byte options, byte sequence,
                                             ReadOnlySpan<byte> payload)
    {
        var frames = new List<byte[]>();
        if (payload.Length <= PktMaxLength)
        {
            frames.Add(EncodeFrame(command, options, sequence, payload));
            return frames;
        }

        int total = payload.Length;
        int offset = 0;
        bool first = true;
        while (offset < total)
        {
            int len = Math.Min(PktMaxLength, total - offset);
            byte opt = (byte)(options | OptChunk);
            int chunkField = offset;
            if (first)
            {
                opt |= OptChunkStart;
                chunkField = total;
            }
            // Match xone: the first chunk and the final data chunk request an
            // acknowledgement to drive the receiver's chunk pump.
            if (first || offset + len >= total) opt |= OptAck;
            frames.Add(EncodeFrameWithChunk(command, opt, sequence, chunkField,
                payload.Slice(offset, len)));
            offset += len;
            first = false;
        }

        frames.Add(EncodeFrameWithChunk(command, (byte)(options | OptChunk), sequence,
            total, ReadOnlySpan<byte>.Empty));
        return frames;
    }

    private static byte[] EncodeFrameWithChunk(byte command, byte options, byte sequence,
        int chunkOffset, ReadOnlySpan<byte> payload)
    {
        Span<byte> lenBytes = stackalloc byte[5];
        Span<byte> offBytes = stackalloc byte[5];
        int lenLen = EncodeVarint((uint)payload.Length, lenBytes);
        int offLen = EncodeVarint((uint)chunkOffset, offBytes);
        int headerLen = 3 + lenLen + offLen;
        bool pad = (headerLen & 1) != 0;

        var buf = new byte[headerLen + (pad ? 1 : 0) + payload.Length];
        buf[0] = command;
        buf[1] = options;
        buf[2] = sequence;
        lenBytes.Slice(0, lenLen).CopyTo(buf.AsSpan(3));
        offBytes.Slice(0, offLen).CopyTo(buf.AsSpan(3 + lenLen));

        int p = headerLen;
        if (pad)
        {
            buf[p - 1] |= 0x80;
            buf[p] = 0x00;
            p++;
        }
        payload.CopyTo(buf.AsSpan(p));
        return buf;
    }

    /// <summary>Decodes a frame header starting at <paramref name="data"/>.
    /// Advances <paramref name="offset"/> past the header (including any
    /// chunk offset and padding). Returns false if the buffer is too short.</summary>
    public static bool TryDecodeHeader(ReadOnlySpan<byte> data, ref int offset,
        out byte command, out byte options, out byte sequence,
        out int payloadLength, out int chunkOffset)
    {
        command = options = sequence = 0;
        payloadLength = 0;
        chunkOffset = 0;
        int start = offset;
        if (data.Length - start < 3) return false;

        command = data[start];
        options = data[start + 1];
        sequence = data[start + 2];
        int p = start + 3;

        if (!TryDecodeVarint(data, ref p, out uint len)) return false;
        payloadLength = (int)len;

        if ((options & OptChunk) != 0)
        {
            if (!TryDecodeVarint(data, ref p, out uint chunk)) return false;
            chunkOffset = (int)chunk;
        }

        offset = p;
        return payloadLength >= 0 && payloadLength <= 0x10000;
    }

    public static int EncodeVarint(uint value, Span<byte> dst)
    {
        int n = 0;
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            dst[n++] = b;
            if (value == 0) break;
            if (n >= dst.Length) break;
        }
        return n;
    }

    private static bool TryDecodeVarint(ReadOnlySpan<byte> data, ref int p, out uint value)
    {
        value = 0;
        int shift = 0;
        while (p < data.Length)
        {
            byte b = data[p++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 28) return false;
        }
        return false;
    }

    public static void WriteU16LE(Span<byte> dst, int off, ushort v)
        => BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(off, 2), v);

    public static void WriteI16LE(Span<byte> dst, int off, short v)
        => WriteU16LE(dst, off, (ushort)v);
}
