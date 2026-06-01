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
        : ParsedTitle;

    // EDCB RecName_Macro.DLL フォーマット:
    // {Title2} ({ServiceName} {YYYY}-{MM}-{DD}-{HHMM}-{曜日})
    private static readonly Regex MetaPattern = new(
        @"^(.+) \((.+) (\d{4})-(\d{2})-(\d{2})-(\d{2})(\d{2})-[^)]+\)$",
        RegexOptions.Compiled);

    private (string title, string station, DateTime? startTime)? _parsed;
    private (string title, string station, DateTime? startTime) Parsed =>
        _parsed ??= ParseFilenameInfo();

    public string ParsedTitle    => Parsed.title;
    public string ParsedStation  => Parsed.station;
    public DateTime? ParsedStartTime => Parsed.startTime;
    public string ParsedStartTimeText => ParsedStartTime.HasValue
        ? $"{ParsedStartTime.Value:yyyy/MM/dd HH:mm}"
        : "";

    private (string title, string station, DateTime? startTime) ParseFilenameInfo()
    {
        var m = MetaPattern.Match(FileName);
        if (!m.Success) return (FileName, "", null);

        var baseTitle = m.Groups[1].Value;
        var station   = m.Groups[2].Value;

        if (int.TryParse(m.Groups[3].Value, out var y)  &&
            int.TryParse(m.Groups[4].Value, out var mo) &&
            int.TryParse(m.Groups[5].Value, out var d)  &&
            int.TryParse(m.Groups[6].Value, out var h)  &&
            int.TryParse(m.Groups[7].Value, out var mi))
        {
            try { return (baseTitle, station, new DateTime(y, mo, d, h, mi, 0)); }
            catch { }
        }

        return (baseTitle, station, null);
    }
}
