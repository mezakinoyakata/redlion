namespace EDCBViewer.Models;

/// <summary>
/// EpgTimer CtrlCmdDef.cs の ReserveData に対応。Reserve.txt の1エントリ。
/// </summary>
public class ReserveData
{
    public uint ReserveID { get; set; }
    public string Title { get; set; } = "";
    public DateTime StartTime { get; set; }
    public uint DurationSecond { get; set; }
    public string StationName { get; set; } = "";
    public ushort OriginalNetworkID { get; set; }
    public ushort TransportStreamID { get; set; }
    public ushort ServiceID { get; set; }
    public ushort EventID { get; set; }
    public string Comment { get; set; } = "";
    public byte OverlapMode { get; set; }
    public DateTime StartTimeEpg { get; set; }
    public uint ReserveStatus { get; set; }
    public List<string> RecFileNameList { get; set; } = [];

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSecond);

    public string DurationText =>
        DurationSecond >= 3600
            ? $"{DurationSecond / 3600}:{DurationSecond % 3600 / 60:D2}:{DurationSecond % 60:D2}"
            : $"{DurationSecond / 60}:{DurationSecond % 60:D2}";

    public string StatusText => ReserveStatus switch
    {
        0 => "通常",
        1 => "録画中",
        2 => "録画済み",
        3 => "無効",
        _ => $"({ReserveStatus})"
    };

    public bool IsRecording => ReserveStatus == 1;

    public string EndTimeText => StartTime.Add(Duration).ToString("HH:mm");
}
