using System.Text;
using EDCBViewer.Models;

namespace EDCBViewer.Parsers;

public static class EpgBinaryParser
{
    public static List<EpgEvent> ParseFile(string filePath)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(filePath); }
        catch { return []; }

        var pos = 0;
        if (bytes.Length < 6) return [];

        // ファイル名から ONID/TSID を読む（バイナリの値と照合用）
        var fn = Path.GetFileNameWithoutExtension(filePath);
        ushort fileOnid = 0, fileTsid = 0;
        if (fn.Length >= 8)
        {
            ushort.TryParse(fn[..4],  System.Globalization.NumberStyles.HexNumber, null, out fileOnid);
            ushort.TryParse(fn[4..8], System.Globalization.NumberStyles.HexNumber, null, out fileTsid);
        }

        // バージョン (WORD)
        _ = ReadU16(bytes, ref pos);

        // EPGDB_SERVICE_INFO（structSize なし、フィールドを直接読む）
        _ = ReadU16(bytes, ref pos); // ONID
        _ = ReadU16(bytes, ref pos); // TSID
        _ = ReadU16(bytes, ref pos); // SID
        _ = ReadByte(bytes, ref pos); // service_type
        _ = ReadByte(bytes, ref pos); // partialReceptionFlag
        _ = ReadString(bytes, ref pos); // service_provider_name
        var serviceName = ReadString(bytes, ref pos);
        _ = ReadString(bytes, ref pos); // network_name
        _ = ReadString(bytes, ref pos); // ts_name
        _ = ReadByte(bytes, ref pos); // remote_control_key_id

        // イベントリスト (vector: vecSize + count + events)
        if (pos + 8 > bytes.Length) return [];
        _ = ReadU32(bytes, ref pos); // vecSize
        var eventCount = ReadU32(bytes, ref pos);

        if (eventCount > 10_000) return []; // 異常値ガード
        var events = new List<EpgEvent>((int)eventCount);
        for (uint i = 0; i < eventCount && pos < bytes.Length; i++)
        {
            try { events.Add(ReadEvent(bytes, ref pos, serviceName)); }
            catch { break; }
        }
        return events;
    }

    private static EpgEvent ReadEvent(byte[] bytes, ref int pos, string serviceName)
    {
        var structSize = ReadU32(bytes, ref pos);
        if (structSize < 16 || structSize > 10_000_000)
            throw new InvalidDataException($"Invalid structSize: {structSize}");
        var start = pos;

        var evt = new EpgEvent
        {
            ONID        = ReadU16(bytes, ref pos),
            TSID        = ReadU16(bytes, ref pos),
            SID         = ReadU16(bytes, ref pos),
            EventID     = ReadU16(bytes, ref pos),
            ServiceName = serviceName,
        };

        var startTimeFlag = ReadByte(bytes, ref pos);
        var st = ReadSystemTime(bytes, ref pos);
        if (startTimeFlag != 0) evt.StartTime = st;

        var durationFlag = ReadByte(bytes, ref pos);
        var durationSec  = ReadU32(bytes, ref pos);
        if (durationFlag != 0) evt.DurationSec = durationSec;

        // ShortInfo
        var shortSize = ReadU32(bytes, ref pos);
        if (shortSize > 0)
        {
            var shortStart = pos;
            evt.EventName = ReadString(bytes, ref pos);
            evt.ShortText = ReadString(bytes, ref pos);
            pos = shortStart + (int)shortSize - 4;
        }

        // ExtInfo
        var extSize = ReadU32(bytes, ref pos);
        if (extSize > 0)
        {
            var extStart = pos;
            evt.ExtText = ReadString(bytes, ref pos);
            pos = extStart + (int)extSize - 4;
        }

        // ContentInfo
        var contentSize = ReadU32(bytes, ref pos);
        if (contentSize > 0)
        {
            var contentStart = pos;
            _ = ReadU32(bytes, ref pos); // nibble vec size
            var nibbleCount = ReadU32(bytes, ref pos);
            if (nibbleCount > 0)
                evt.ContentNibble = ReadByte(bytes, ref pos);
            pos = contentStart + (int)contentSize - 4;
        }

        // ComponentInfo / AudioInfo / EventGroupInfo / EventRelayInfo (skip)
        foreach (var _ in (int[])[0, 0, 0, 0])
        {
            var sz = ReadU32(bytes, ref pos);
            if (sz > 0) pos += (int)sz - 4;
        }

        if (pos < bytes.Length)
            evt.FreeCAFlag = ReadByte(bytes, ref pos);

        // structSize で終端に移動（フォーマット差異を吸収）
        pos = start + (int)structSize - 4;
        return evt;
    }

    private static DateTime? ReadSystemTime(byte[] bytes, ref int pos)
    {
        var year  = ReadU16(bytes, ref pos);
        var month = ReadU16(bytes, ref pos);
        _         = ReadU16(bytes, ref pos); // wDayOfWeek
        var day   = ReadU16(bytes, ref pos);
        var hour  = ReadU16(bytes, ref pos);
        var min   = ReadU16(bytes, ref pos);
        var sec   = ReadU16(bytes, ref pos);
        var ms    = ReadU16(bytes, ref pos);

        if (year == 0 || month is 0 or > 12) return null;
        try { return new DateTime(year, month, day, hour, min, sec, ms, DateTimeKind.Local); }
        catch { return null; }
    }

    private static string ReadString(byte[] bytes, ref int pos)
    {
        if (pos + 4 > bytes.Length) return "";
        var size = ReadU32(bytes, ref pos); // データのバイト数のみ（DWORD自体を含まない）
        if (size == 0) return "";
        var dataLen = (int)size;
        if (dataLen < 0 || pos + dataLen > bytes.Length) return "";
        var s = Encoding.Unicode.GetString(bytes, pos, dataLen).TrimEnd('\0');
        pos += dataLen;
        return s;
    }

    private static byte ReadByte(byte[] bytes, ref int pos)
        => pos < bytes.Length ? bytes[pos++] : (byte)0;

    private static ushort ReadU16(byte[] bytes, ref int pos)
    {
        if (pos + 2 > bytes.Length) return 0;
        var v = BitConverter.ToUInt16(bytes, pos); pos += 2; return v;
    }

    private static uint ReadU32(byte[] bytes, ref int pos)
    {
        if (pos + 4 > bytes.Length) return 0;
        var v = BitConverter.ToUInt32(bytes, pos); pos += 4; return v;
    }
}
