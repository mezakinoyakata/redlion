namespace EDCBViewer.Models;

public class EpgEvent
{
    public ushort ONID { get; set; }
    public ushort TSID { get; set; }
    public ushort SID { get; set; }
    /// <summary>
    /// 実 EPG の event_id は 16bit だが、しょぼカルから作った合成行は
    /// 1000000 以上を使うため int で持つ（EpgDbReader.SyntheticEventIdBase）。
    /// </summary>
    public int EventID { get; set; }
    public string ServiceName { get; set; } = "";
    public DateTime? StartTime { get; set; }
    public uint? DurationSec { get; set; }
    public string EventName { get; set; } = "";
    public string ShortText { get; set; } = "";
    public string ExtText { get; set; } = "";
    public byte? ContentNibble { get; set; }
    public byte FreeCAFlag { get; set; }
}
