using System;
using System.Buffers.Binary;

namespace GKMD.Internal.Usbip;

/// Sony DualSense test command protocol handler.

public sealed class SonyTestCommandHandler
{
    private static readonly byte[] _serialNumber = EncodeShiftJIS("ZC325ZC799ZC325ZC");
    private static readonly byte[] _pcbaId = { 0x46, 0x9A, 0x35, 0xE4, 0xE4, 0x08 };
    private static readonly byte[] _uniqueId = { 0x3C, 0xB0, 0xF6, 0xBF, 0x60, 0x17, 0x9A, 0x6A, 0x00 };
    private static readonly byte[] _bdMacAddress = { 0xDB, 0x1C, 0xB2, 0x4B, 0x2F, 0xD4 };
    private static readonly byte[] _batteryBarcode = EncodeShiftJIS("49945712EA251215C701272");
    private static readonly byte[] _vcmLeftBarcode = EncodeShiftJIS("BS31V25C09N05950");
    private static readonly byte[] _vcmRightBarcode = EncodeShiftJIS("BS31V25C09N05778");
    private static readonly byte[] _assemblePartsInfo =
    {
        0x05, 0x06, 0x00, 0x0C, 0x02, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };
    private const ushort BatteryVoltageMv = 3686;

    private static readonly byte[] _telemetryPage0 = CreateTelemetryPage0();
    private static readonly byte[] _telemetryPage1 = CreateTelemetryPage1();
    private static readonly byte[] _telemetryPage2 = CreateTelemetryPage2();
    private static readonly byte[] _telemetryPage3 = CreateTelemetryPage3();

    internal readonly byte[][] _actionResponses = new byte[38][];

    public SonyTestCommandHandler()
    {
        _actionResponses[4] = BuildResponse(4, _pcbaId);
        _actionResponses[9] = BuildResponse(9, _uniqueId);
        _actionResponses[17] = BuildResponse(17, _pcbaId, 18);
        _actionResponses[19] = BuildResponse(19, _serialNumber, 17);
        _actionResponses[21] = BuildResponse(21, _assemblePartsInfo);
        _actionResponses[24] = BuildResponse(24, _batteryBarcode);
        _actionResponses[26] = BuildResponse(26, _vcmLeftBarcode);
        _actionResponses[28] = BuildResponse(28, _vcmRightBarcode);
    }

    public int HandleTestCommand(ReadOnlySpan<byte> data, Span<byte> responseBuffer)
    {
        if (data.Length < 2) return -1;
        byte deviceId = data[0];
        byte actionId = data[1];
        if (actionId >= _actionResponses.Length) return -1;
        byte[] baseResponse = _actionResponses[actionId];
        if (baseResponse == null) return -1;
        int copyLen = Math.Min(baseResponse.Length + 1, responseBuffer.Length);
        responseBuffer[0] = 0x81;
        baseResponse.CopyTo(responseBuffer.Slice(1));
        return copyLen;
    }

    public int HandleType2Tracability(byte param, Span<byte> responseBuffer)
    {
        int maxLen = Math.Min(43, responseBuffer.Length);
        if (maxLen < 4) return -1;
        responseBuffer.Slice(0, maxLen).Clear();
        responseBuffer[0] = 0x00;
        responseBuffer[1] = 0x00;
        responseBuffer[2] = 0x00;
        responseBuffer[3] = 0x00;
        return maxLen;
    }

    public int HandleTelemetry(byte[] pages, int maxPages)
    {
        byte[][] pageData = { _telemetryPage0, _telemetryPage1, _telemetryPage2, _telemetryPage3 };
        int count = Math.Min(maxPages, 4);
        for (int i = 0; i < count; i++)
            pageData[i].CopyTo(pages, i * 56);
        return -count;
    }

    public int HandleBtMac(Span<byte> responseBuffer)
    {
        if (responseBuffer.Length < 6) return -1;
        _bdMacAddress.CopyTo(responseBuffer);
        return 6;
    }

    public int HandleBatteryVoltage(Span<byte> responseBuffer)
    {
        if (responseBuffer.Length < 4) return -1;
        BinaryPrimitives.WriteUInt16LittleEndian(responseBuffer[0..], BatteryVoltageMv);
        return 4;
    }

    public int HandleBtPatchInfo(Span<byte> responseBuffer)
    {
        if (responseBuffer.Length < 4) return -1;
        BinaryPrimitives.WriteUInt32LittleEndian(responseBuffer[0..], 0x00000019);
        return 4;
    }

    private byte[] BuildResponse(byte actionId, byte[] data, int extraPad = 0)
    {
        int totalLen = 3 + data.Length + extraPad;
        var result = new byte[totalLen];
        result[0] = 0x01;
        result[1] = actionId;
        result[2] = 0x02;
        data.CopyTo(result.AsSpan(3));
        return result;
    }

    private static byte[] EncodeShiftJIS(string s)
    {
        var result = new byte[Math.Max(1, s.Length)];
        int i = 0;
        foreach (char c in s)
        {
            byte b = (byte)c;
            if (b <= 0x7F || (b >= 0xA1 && b <= 0xDF))
                result[i] = b;
            else
                result[i] = 0x3F;
            i++;
        }
        return result;
    }

    private static byte[] CreateTelemetryPage0()
    {
        var page = new byte[56];
        string serial = "ZC325ZC799ZC325ZC";
        for (int i = 0; i < 17; i++)
            page[i] = (byte)(i < serial.Length ? serial[i] : 0x00);
        BinaryPrimitives.WriteUInt32LittleEndian(page[17..], 359);
        BinaryPrimitives.WriteInt32LittleEndian(page[21..], 1908245);
        BinaryPrimitives.WriteInt32LittleEndian(page[25..], 483737);
        BinaryPrimitives.WriteInt32LittleEndian(page[29..], 50007);
        BinaryPrimitives.WriteInt32LittleEndian(page[33..], 9373);
        BinaryPrimitives.WriteInt32LittleEndian(page[37..], 14716);
        BinaryPrimitives.WriteUInt16LittleEndian(page[41..], 139);
        BinaryPrimitives.WriteUInt16LittleEndian(page[43..], 277);
        BinaryPrimitives.WriteUInt16LittleEndian(page[45..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(page[47..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(page[49..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(page[51..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(page[53..], 0);
        return page;
    }

    private static byte[] CreateTelemetryPage1()
    {
        var page = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(page[3..], 0);
        page[5] = 0;
        page[6] = 0;
        page[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(page[8..], 5);
        BinaryPrimitives.WriteUInt16LittleEndian(page[10..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(page[12..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(page[14..], 282);
        BinaryPrimitives.WriteUInt16LittleEndian(page[16..], 63);
        BinaryPrimitives.WriteUInt16LittleEndian(page[18..], 11);
        BinaryPrimitives.WriteUInt16LittleEndian(page[20..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[22..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[26..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[30..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[34..], 77427);
        BinaryPrimitives.WriteUInt16LittleEndian(page[38..], 2838);
        BinaryPrimitives.WriteUInt32LittleEndian(page[40..], 67728);
        BinaryPrimitives.WriteUInt16LittleEndian(page[44..], 2816);
        BinaryPrimitives.WriteUInt32LittleEndian(page[46..], 45913);
        BinaryPrimitives.WriteUInt16LittleEndian(page[50..], 2817);
        BinaryPrimitives.WriteUInt32LittleEndian(page[52..], 31453);
        return page;
    }

    private static byte[] CreateTelemetryPage2()
    {
        var page = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(page[0..], 2731);
        WriteButtonCount(page[2..], 12030);
        WriteButtonCount(page[6..], 19983);
        WriteButtonCount(page[10..], 8505);
        WriteButtonCount(page[14..], 13403);
        WriteButtonCount(page[18..], 30048);
        WriteButtonCount(page[22..], 53737);
        WriteButtonCount(page[26..], 58780);
        WriteButtonCount(page[30..], 5205);
        WriteButtonCount(page[34..], 16374);
        WriteButtonCount(page[38..], 25764);
        WriteButtonCount(page[42..], 2645);
        WriteButtonCount(page[46..], 19781);
        WriteButtonCount(page[50..], 35668);
        return page;
    }

    private static byte[] CreateTelemetryPage3()
    {
        var page = new byte[56];
        WriteButtonCount(page[0..], 2223);
        WriteButtonCount(page[4..], 23892);
        WriteButtonCount(page[8..], 4249);
        WriteButtonCount(page[12..], 4190);
        WriteButtonCount(page[16..], 1292);
        WriteButtonCount(page[20..], 739);
        WriteButtonCount(page[24..], 169);
        return page;
    }

    private static void WriteButtonCount(Span<byte> dest, int count)
    {
        ushort low = (ushort)(count & 0xFFFF);
        ushort high = count >= 65536 ? (ushort)0xFF00 : (ushort)0;
        dest[0] = (byte)(low & 0xFF);
        dest[1] = (byte)((low >> 8) & 0xFF);
        dest[2] = (byte)(high & 0xFF);
        dest[3] = (byte)((high >> 8) & 0xFF);
    }
}