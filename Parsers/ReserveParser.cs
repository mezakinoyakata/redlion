using EDCBViewer.Models;

namespace EDCBViewer.Parsers;

/// <summary>
/// EpgTimerSrv が出力する Reserve.txt（タブ区切りテキスト）を読み込む。
/// CParseReserveText::SaveLine の実際のフォーマットに対応:
/// date(YYYY/MM/DD), time(HH:MM:SS), duration(HH:MM:SS), title, stationName,
/// onid, tsid, sid, eid, priority, tuijyuu, reserveID, recMode, pittari,
/// batFilePath, "0", comment, firstRecFolder, suspendMode, rebootFlag, "",
/// useMargine, startMargine, endMargine, serviceMode,
/// epgDate, epgTime, extraFolderCount, [extraFolders...],
/// continueRec, partialRec, tunerID, reserveStatus,
/// partialFolderCount, [partialFolders...]
/// </summary>
public static class ReserveParser
{
    public static List<ReserveData> Load(string path)
    {
        var list = new List<ReserveData>();
        if (!File.Exists(path))
            return list;

        // ファイルを一括でメモリに読み込んで即クローズ — ハンドル保持時間を最小化する
        // Reserve.txtには絶対に書き込まない
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

        list.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        return list;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static ReserveData? ParseLine(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 12)
            return null;

        try
        {
            var data = new ReserveData();
            int i = 0;
            data.StartTime          = ParseDateTime(f[i++], f[i++]);   // 0+1 (date+time 別フィールド)
            data.DurationSecond     = ParseDuration(f[i++]);            // 2
            data.Title              = f[i++];                           // 3
            data.StationName        = f[i++];                           // 4
            data.OriginalNetworkID  = ParseUShort(f[i++]);              // 5
            data.TransportStreamID  = ParseUShort(f[i++]);              // 6
            data.ServiceID          = ParseUShort(f[i++]);              // 7
            data.EventID            = ParseUShort(f[i++]);              // 8
            i++;                                                         // 9  priority
            i++;                                                         // 10 tuijyuuFlag
            data.ReserveID          = ParseUInt(f[i++]);                // 11
            i++;                                                         // 12 recMode
            i++;                                                         // 13 pittariFlag
            i++;                                                         // 14 batFilePath
            i++;                                                         // 15 "0"
            data.Comment            = f[i++];                           // 16
            i++;                                                         // 17 firstRecFolder
            i++;                                                         // 18 suspendMode
            i++;                                                         // 19 rebootFlag
            i++;                                                         // 20 ""
            i++;                                                         // 21 useMargineFlag
            i++;                                                         // 22 startMargine
            i++;                                                         // 23 endMargine
            i++;                                                         // 24 serviceMode
            if (i + 1 < f.Length)
                data.StartTimeEpg   = ParseDateTime(f[i++], f[i++]);   // 25+26

            // 追加録画フォルダ (可変長)
            if (i < f.Length && uint.TryParse(f[i++], out var extraCount))
            {
                for (var j = 0; j < extraCount && i < f.Length; j++, i++)
                    data.RecFileNameList.Add(f[i]);
            }

            i++;                                                         // continueRecFlag
            i++;                                                         // partialRecFlag
            i++;                                                         // tunerID
            if (i < f.Length) data.ReserveStatus = ParseUInt(f[i++]);  // reserveStatus

            return data;
        }
        catch
        {
            return null;
        }
    }

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
}
