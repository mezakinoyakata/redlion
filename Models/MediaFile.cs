using System.Text.RegularExpressions;

namespace EDCBViewer.Models;

public class MediaFile
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsDirectory { get; set; } = false;

    public string DisplayName => IsDirectory
        ? "📁 " + Path.GetFileName(FilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        : Parsed.contIdx.HasValue
            ? $"{ParsedTitle} (続き{Parsed.contIdx})"
            : ParsedTitle;

    // EDCB RecName_Macro.DLL フォーマット:
    // {Title2} ({ServiceName} {YYYY}-{MM}-{DD}-{HHMM}-{曜日})
    // EDCB の継続録画サフィックス -(N) を任意で許容し、番号をグループ8で捕捉する
    private static readonly Regex MetaPattern = new(
        @"^(.+) \((.+) (\d{4})-(\d{2})-(\d{2})-(\d{2})(\d{2})-[^)]+\)(?:-\((\d+)\))?$",
        RegexOptions.Compiled);

    private (string title, string station, DateTime? startTime, int? contIdx)? _parsed;
    private (string title, string station, DateTime? startTime, int? contIdx) Parsed =>
        _parsed ??= ParseFilenameInfo();

    public string ParsedTitle    => Parsed.title;
    public string ParsedStation  => Parsed.station;
    public DateTime? ParsedStartTime => Parsed.startTime;
    public string ParsedStartTimeText => ParsedStartTime.HasValue
        ? $"{ParsedStartTime.Value:yyyy/MM/dd HH:mm}"
        : "";

    private (string title, string station, DateTime? startTime, int? contIdx) ParseFilenameInfo()
    {
        var m = MetaPattern.Match(FileName);
        if (!m.Success) return (FileName, "", null, null);

        var baseTitle = m.Groups[1].Value;
        var station   = m.Groups[2].Value;
        int? contIdx  = m.Groups[8].Success && int.TryParse(m.Groups[8].Value, out var ci)
                        ? ci : null;

        if (int.TryParse(m.Groups[3].Value, out var y)  &&
            int.TryParse(m.Groups[4].Value, out var mo) &&
            int.TryParse(m.Groups[5].Value, out var d)  &&
            int.TryParse(m.Groups[6].Value, out var h)  &&
            int.TryParse(m.Groups[7].Value, out var mi))
        {
            try { return (baseTitle, station, new DateTime(y, mo, d, h, mi, 0), contIdx); }
            catch { }
        }

        return (baseTitle, station, null, contIdx);
    }
}
