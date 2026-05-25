using EDCBViewer.Models;

namespace EDCBViewer.Parsers;

/// <summary>
/// EpgTimerSrv が出力する RecInfo.txt（タブ区切りテキスト）を読み込む。
/// CParseRecInfoText::SaveLine の実際のフォーマットに対応:
/// recFilePath, title, date(YYYY/MM/DD), time(HH:MM:SS), duration(HH:MM:SS),
/// serviceName, onid, tsid, sid, eid, drops, scrambles, recStatus,
/// epgDate, epgTime, comment, protectFlag, id
/// </summary>
public static class RecInfoParser
{
    public static List<RecFileInfo> Load(string path)
    {
        var list = new List<RecFileInfo>();
        if (!File.Exists(path))
            return list;

        // ファイルを一括でメモリに読み込んで即クローズ — ハンドル保持時間を最小化する
        byte[] raw;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            raw = new byte[fs.Length];
            fs.ReadExactly(raw);
        }

        var encoding = EncodingDetector.DetectFromBytes(raw);
        foreach (var line in encoding.GetString(raw).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(";;") || string.IsNullOrWhiteSpace(trimmed))
                continue;

            var item = ParseLine(trimmed);
            if (item != null)
                list.Add(item);
        }

        list.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
        return list;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static RecFileInfo? ParseLine(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 13)
            return null;

        try
        {
            var info = new RecFileInfo();
            int i = 0;
            info.RecFilePath        = f[i++];                               // 0
            info.Title              = f[i++];                               // 1
            info.StartTime          = ParseDateTime(f[i++], f[i++]);        // 2+3 (date+time 別フィールド)
            info.DurationSecond     = ParseDuration(f[i++]);                // 4
            info.ServiceName        = f[i++];                               // 5
            info.OriginalNetworkID  = ParseUShort(f[i++]);                  // 6
            info.TransportStreamID  = ParseUShort(f[i++]);                  // 7
            info.ServiceID          = ParseUShort(f[i++]);                  // 8
            info.EventID            = ParseUShort(f[i++]);                  // 9
            info.Drops              = ParseLong(f[i++]);                    // 10
            info.Scrambles          = ParseLong(f[i++]);                    // 11
            info.RecStatus          = ParseUInt(f[i++]);                    // 12
            if (i + 1 < f.Length)
                info.StartTimeEpg   = ParseDateTime(f[i++], f[i++]);        // 13+14
            if (i < f.Length) info.Comment      = f[i++];                  // 15
            if (i < f.Length) info.ProtectFlag  = ParseByte(f[i++]);       // 16
            if (i < f.Length) info.ID           = ParseUInt(f[i++]);       // 17
            return info;
        }
        catch
        {
            return null;
        }
    }

    // date="YYYY/MM/DD", time="HH:MM:SS" の2フィールドを結合してDateTime化
    private static DateTime ParseDateTime(string date, string time)
    {
        return DateTime.TryParseExact($"{date} {time}", "yyyy/MM/dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : DateTime.MinValue;
    }

    private static uint ParseDuration(string s)
    {
        var p = s.Split(':');
        if (p.Length == 3 &&
            uint.TryParse(p[0], out var h) &&
            uint.TryParse(p[1], out var m) &&
            uint.TryParse(p[2], out var sec))
            return h * 3600 + m * 60 + sec;
        return 0;
    }

    private static ushort ParseUShort(string s) => ushort.TryParse(s, out var v) ? v : (ushort)0;
    private static uint   ParseUInt(string s)   => uint.TryParse(s, out var v)   ? v : 0u;
    private static long   ParseLong(string s)   => long.TryParse(s, out var v)   ? v : 0L;
    private static byte   ParseByte(string s)   => byte.TryParse(s, out var v)   ? v : (byte)0;
}
