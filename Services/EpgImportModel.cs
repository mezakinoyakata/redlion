namespace EDCBViewer.Services;

/// <summary>
/// 取得元(EpgDataCap3.dll)と書き出し先(PgWriter)の間でやり取りする素の EPG データ。
///
/// EDCB のネイティブ構造体にも Npgsql にも依存しない。
/// </summary>
public sealed class EpgService
{
    public ushort Onid, Tsid, Sid;
    public byte   ServiceType, PartialReception, RemoteControlKey;
    public string ProviderName = "", ServiceName = "", NetworkName = "", TsName = "";
}

/// <summary>番組ジャンル(content_nibble)。1番組に複数付くことがある。</summary>
public sealed class EpgGenre
{
    public byte L1, L2, User1, User2;
}

public sealed class EpgEventRow
{
    public ushort    Onid, Tsid, Sid, EventId;
    /// <summary>開始時刻未定(StartTimeFlag=0)の番組があるため null 許容。</summary>
    public DateTime? StartTime;
    public uint?     DurationSec;
    public string    EventName = "", ShortText = "", ExtText = "";
    public byte?     ComponentStreamContent, ComponentType, ComponentTag;
    public string    ComponentText = "";
    public byte      FreeCaFlag;
    public List<EpgGenre> Genres = new();
}
