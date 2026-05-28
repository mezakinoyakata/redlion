using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Xml.Linq;
using EDCBViewer.Models;

namespace EDCBViewer.Parsers;

/// <summary>
/// EpgTimerSrv の HTTP API (XML) からデータを取得するクライアント。
/// エンドポイント:
///   GET /api/EnumRecInfo?count=65535          → 録画済み一覧
///   GET /api/EnumReserveInfo?count=65535       → 予約一覧
///   GET /api/EnumRecInfo?id=N                  → ID 指定単一取得
///
/// レスポンス形式:
///   &lt;entry&gt;
///     &lt;total&gt;N&lt;/total&gt;
///     &lt;items&gt;
///       &lt;recinfo&gt; or &lt;reserve&gt; ...
///     &lt;/items&gt;
///   &lt;/entry&gt;
/// </summary>
public class EmwuiClient(string baseUrl)
{
    // EDCB サーバーは Connection: close のため毎回 DNS 解決 + TCP 接続が発生する。
    // ホスト名が IPv6 link-local (fe80::) と IPv4 両方に解決される場合、
    // .NET が IPv6 を先に試みてタイムアウト (~15 秒) してから IPv4 に切り替えるため極めて遅い。
    // ConnectCallback で IPv4 のみに強制することでこの遅延を排除する。
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (ctx, ct) =>
        {
            // IPv4 アドレスのみ取得して接続
            var addrs = await Dns.GetHostAddressesAsync(
                ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
            if (addrs.Length == 0)
                throw new InvalidOperationException(
                    $"IPv4 アドレスが解決できません: {ctx.DnsEndPoint.Host}");
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await sock.ConnectAsync(addrs[0], ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(sock, ownsSocket: true);
            }
            catch { sock.Dispose(); throw; }
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public readonly string Base = baseUrl.TrimEnd('/');

    // ── 録画済みリスト ────────────────────────────────────────────────────────────

    /// <summary>録画済み一覧を取得。index=先頭オフセット、count=取得件数、total=サーバー上の全件数。</summary>
    public async Task<(List<RecFileInfo> items, int total)> GetRecFileInfoAsync(
        int count = 100, int index = 0)
    {
        var xml = await _http.GetStringAsync(
            $"{Base}/api/EnumRecInfo?count={count}&index={index}");
        return ParseItemsWithTotal(xml, ParseRecItem);
    }

    /// <summary>指定 ID の recFilePath のみ返す（PlayServer から使用）。</summary>
    public async Task<string?> GetRecFilePathAsync(uint id)
    {
        var xml = await _http.GetStringAsync($"{Base}/api/EnumRecInfo?id={id}");
        return ParseItemsWithTotal(xml, ParseRecItem).items.FirstOrDefault()?.RecFilePath;
    }

    private static RecFileInfo? ParseRecItem(XElement e)
    {
        try
        {
            return new RecFileInfo
            {
                ID                = GetUInt(e,   "ID"),
                RecFilePath       = GetStr(e,    "recFilePath"),
                Title             = GetStr(e,    "title"),
                OriginalNetworkID = GetUShort(e, "ONID"),
                TransportStreamID = GetUShort(e, "TSID"),
                ServiceID         = GetUShort(e, "SID"),
                EventID           = GetUShort(e, "eventID"),
                ServiceName       = GetStr(e,    "service_name"),
                StartTime         = GetDateTime(e, "startDate",    "startTime"),
                StartTimeEpg      = GetDateTime(e, "startDateEpg", "startTimeEpg"),
                DurationSecond    = GetUInt(e,   "duration"),
                Drops             = GetLong(e,   "drops"),
                Scrambles         = GetLong(e,   "scrambles"),
                RecStatus         = GetUInt(e,   "recStatus"),
                Comment           = GetStr(e,    "comment"),
                ProgramInfo       = GetStr(e,    "programInfo"),
                ErrInfo           = GetStr(e,    "errInfo"),
                ProtectFlag       = GetByte(e,   "protect"),
            };
        }
        catch { return null; }
    }

    // ── 予約リスト ────────────────────────────────────────────────────────────────

    public async Task<List<ReserveData>> GetReserveInfoAsync()
    {
        var xml = await _http.GetStringAsync($"{Base}/api/EnumReserveInfo?count=65535");
        return ParseItemsWithTotal(xml, ParseResItem).items;
    }

    private static ReserveData? ParseResItem(XElement e)
    {
        try
        {
            // recsetting.recEnabled=0 → 無効予約（ReserveStatus=3）
            var recSetting = e.Element("recsetting");
            var recEnabled = recSetting == null || GetUInt(recSetting, "recEnabled") != 0;

            var data = new ReserveData
            {
                // 予約XMLのIDフィールドは <ID>（recinfo と同じ名前）
                ReserveID         = GetUInt(e,   "ID"),
                Title             = GetStr(e,    "title"),
                StartTime         = GetDateTime(e, "startDate", "startTime"),
                DurationSecond    = GetUInt(e,   "duration"),
                StationName       = GetStr(e,    "service_name"),
                OriginalNetworkID = GetUShort(e, "ONID"),
                TransportStreamID = GetUShort(e, "TSID"),
                ServiceID         = GetUShort(e, "SID"),
                EventID           = GetUShort(e, "eventID"),
                Comment           = GetStr(e,    "comment"),
                OverlapMode       = GetByte(e,   "overlapMode"),
                // reserveStatus タグが存在する場合は読む。
                // 存在しない場合は recEnabled=0 なら無効(3)、それ以外は通常(0)。
                ReserveStatus     = e.Element("reserveStatus") != null
                                        ? GetUInt(e, "reserveStatus")
                                        : recEnabled ? 0u : 3u,
            };

            // 録画ファイル名リスト（録画済み予約に存在する場合）
            var fnList = e.Element("recFileNameList");
            if (fnList != null)
                foreach (var fn in fnList.Elements())
                    data.RecFileNameList.Add(fn.Value);

            return data;
        }
        catch { return null; }
    }

    // ── 番組情報（EnumRecInfo?id=N）────────────────────────────────────────────────

    /// <summary>
    /// 指定 ID の録画情報から番組説明テキストを取得する。
    /// EnumRecInfo?id=N は programInfo フィールドにフル内容が入っている。
    /// （count=N の一括取得版は programInfo が空のため、行選択時に個別取得する）
    /// 取得できなかった場合は "" を返す（例外は内部で握りつぶす）。
    /// </summary>
    public async Task<string> GetProgramInfoTextAsync(uint id, CancellationToken ct = default)
    {
        try
        {
            var xml = await _http.GetStringAsync($"{Base}/api/EnumRecInfo?id={id}", ct);
            var (items, _) = ParseItemsWithTotal(xml, ParseRecItem);
            var raw = items.FirstOrDefault()?.ProgramInfo ?? "";
            return ExtractDescFromProgramInfo(raw);
        }
        catch { return ""; }
    }

    /// <summary>
    /// programInfo テキスト（EMWUI の ProgramInfo 形式）から番組説明部分を抽出する。
    /// 形式:
    ///   行1: 日付時刻
    ///   行2: サービス名
    ///   行3: タイトル
    ///   (空行)
    ///   概要テキスト
    ///   (空行)
    ///   詳細情報
    ///   詳細テキスト
    ///   (空行)
    ///   ジャンル : ...
    ///   映像 : ...
    ///   OriginalNetworkID:...
    /// </summary>
    private static string ExtractDescFromProgramInfo(string programInfo)
    {
        if (string.IsNullOrWhiteSpace(programInfo)) return "";

        var lines = programInfo.Split('\n');

        // ヘッダー（日付・サービス・タイトル）の 3 行をスキップし、その後の空行もスキップ
        int start = 3;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;

        // 「ジャンル :」「映像 :」「OriginalNetworkID:」で終端を検出
        int end = lines.Length;
        for (int i = start; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("ジャンル :") ||
                lines[i].StartsWith("映像 :") ||
                lines[i].StartsWith("OriginalNetworkID:"))
            {
                end = i;
                break;
            }
        }

        // 末尾の空行を除去
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;

        return string.Join('\n', lines[start..end]).Trim();
    }

    // ── 共通 XML パーサー ──────────────────────────────────────────────────────────

    /// <summary>
    /// &lt;entry&gt;&lt;total&gt;N&lt;/total&gt;&lt;items&gt;...&lt;/items&gt;&lt;/entry&gt; を解析する。
    /// items 直下の全子要素を対象とするため、recinfo / reserve など名前に依存しない。
    /// total はサーバー上の全件数（取得件数より多い場合あり）。
    /// </summary>
    private static (List<T> items, int total) ParseItemsWithTotal<T>(
        string xml, Func<XElement, T?> parse) where T : class
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        int.TryParse(root?.Element("total")?.Value, out var total);

        var itemsEl = root?.Element("items");
        if (itemsEl == null) return ([], total);

        var list = new List<T>();
        foreach (var elem in itemsEl.Elements())
        {
            var item = parse(elem);
            if (item != null) list.Add(item);
        }
        return (list, total);
    }

    // ── XML ヘルパー ──────────────────────────────────────────────────────────────

    private static string GetStr(XElement e, string name)
        => e.Element(name)?.Value ?? "";

    private static uint GetUInt(XElement e, string name)
        => uint.TryParse(e.Element(name)?.Value, out var v) ? v : 0u;

    private static ushort GetUShort(XElement e, string name)
        => ushort.TryParse(e.Element(name)?.Value, out var v) ? v : (ushort)0;

    private static long GetLong(XElement e, string name)
        => long.TryParse(e.Element(name)?.Value, out var v) ? v : 0L;

    private static byte GetByte(XElement e, string name)
        => byte.TryParse(e.Element(name)?.Value, out var v) ? v : (byte)0;

    /// <summary>
    /// startDate="2026/05/27" + startTime="20:54:00" を DateTime に変換。
    /// </summary>
    private static DateTime GetDateTime(XElement e, string dateKey, string timeKey)
    {
        var d = e.Element(dateKey)?.Value ?? "";
        var t = e.Element(timeKey)?.Value ?? "";
        return DateTime.TryParseExact($"{d} {t}", "yyyy/MM/dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) ? dt : DateTime.MinValue;
    }
}
