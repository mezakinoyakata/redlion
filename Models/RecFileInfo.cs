namespace EDCBViewer.Models;

/// <summary>
/// EpgTimer CtrlCmdDef.cs の RecFileInfo に対応。RecInfo.txt の1エントリ。
/// </summary>
public class RecFileInfo
{
    public uint ID { get; set; }
    public string RecFilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime StartTime { get; set; }
    public uint DurationSecond { get; set; }
    public string ServiceName { get; set; } = "";
    public ushort OriginalNetworkID { get; set; }
    public ushort TransportStreamID { get; set; }
    public ushort ServiceID { get; set; }
    public ushort EventID { get; set; }
    public long Drops { get; set; }
    public long Scrambles { get; set; }
    public uint RecStatus { get; set; }
    public DateTime StartTimeEpg { get; set; }
    public string Comment { get; set; } = "";
    public string ProgramInfo { get; set; } = "";
    public string ErrInfo { get; set; } = "";
    public byte ProtectFlag { get; set; }

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSecond);

    public string DurationText =>
        DurationSecond >= 3600
            ? $"{DurationSecond / 3600}:{DurationSecond % 3600 / 60:D2}:{DurationSecond % 60:D2}"
            : $"{DurationSecond / 60}:{DurationSecond % 60:D2}";

    // EpgTimer DefineEnum.cs の RecEndStatus に対応
    public string RecStatusText => RecStatus switch
    {
        0  => "録画前",
        1  => "録画終了",
        2  => "チューナーオープン失敗",
        3  => "録画中断",
        4  => "次予約で中断",
        5  => "起動遅れ",
        6  => "開始時間変更",
        7  => "チューナー不足",
        8  => "無効",
        9  => "番組情報なし",
        10 => "番組未検出",
        11 => "録画終了(別フォルダ)",
        12 => "録画開始失敗",
        13 => "一部録画",
        14 => "チャンネル変更失敗",
        15 => "ファイル保存エラー",
        _  => $"({RecStatus})"
    };

    public bool IsNormalEnd => RecStatus == 1 || RecStatus == 11;

    public bool HasDrops => Drops > 0 || Scrambles > 0;

    public string FileName => Path.GetFileName(RecFilePath);
}
