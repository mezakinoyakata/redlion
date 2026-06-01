namespace EDCBViewer.Models;

public class EpgEvent
{
    public ushort ONID { get; set; }
    public ushort TSID { get; set; }
    public ushort SID { get; set; }
    public ushort EventID { get; set; }
    public string ServiceName { get; set; } = "";
    public DateTime? StartTime { get; set; }
    public uint? DurationSec { get; set; }
    public string EventName { get; set; } = "";
    public string ShortText { get; set; } = "";
    public string ExtText { get; set; } = "";
    public byte? ContentNibble { get; set; }
    public byte FreeCAFlag { get; set; }
}
