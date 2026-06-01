namespace EDCBViewer.Models;

public class RecordingIndexEntry
{
    public string FileName { get; set; } = "";
    public string SeriesTitle { get; set; } = "";
    public int? EpisodeNumber { get; set; }
    public string FullTitle { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime StartTimeEpg { get; set; }
    public uint DurationSecond { get; set; }
    public string ServiceName { get; set; } = "";
    public uint RecId { get; set; }
    public ushort OriginalNetworkID { get; set; }
    public ushort TransportStreamID { get; set; }
    public ushort ServiceID { get; set; }
    public ushort EventID { get; set; }
    public string ProgramInfo { get; set; } = "";
    public string Comment { get; set; } = "";
    public string ErrInfo { get; set; } = "";
    public long Drops { get; set; }
    public long Scrambles { get; set; }
    public uint RecStatus { get; set; }
    public byte ProtectFlag { get; set; }
    public string OriginalFilePath { get; set; } = "";
    public DateTime SavedAt { get; set; }

    public string DurationText =>
        DurationSecond >= 3600
            ? $"{DurationSecond / 3600}:{DurationSecond % 3600 / 60:D2}:{DurationSecond % 60:D2}"
            : $"{DurationSecond / 60}:{DurationSecond % 60:D2}";

    public bool HasDrops => Drops > 0 || Scrambles > 0;
}
