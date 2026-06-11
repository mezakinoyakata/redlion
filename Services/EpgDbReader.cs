using MySqlConnector;
using EDCBViewer.Models;

namespace EDCBViewer.Services;

public sealed class EpgDbReader
{
    private readonly string _connStr;

    public EpgDbReader(string connStr) => _connStr = connStr;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connStr);

    public string? GetEventInfoTextByStationAndTime(
        string stationName, DateTime startTime, string? preferTitle = null)
    {
        if (!IsConfigured || string.IsNullOrEmpty(stationName)) return null;
        try
        {
            using var conn = new MySqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // 同局名で複数サービスが存在する場合に備え最大10件取得し、
            // タイトル一致率でベストを選ぶ。
            cmd.CommandText =
                "SELECT e.short_text, e.ext_text, e.event_name FROM events e " +
                "JOIN services s ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid " +
                "WHERE s.service_name=@svc AND e.start_time >= @lo AND e.start_time <= @hi " +
                "ORDER BY e.start_time ASC LIMIT 10";
            cmd.Parameters.AddWithValue("@svc", stationName);
            cmd.Parameters.AddWithValue("@lo", startTime.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@hi", startTime.AddMinutes(2).ToString("yyyy-MM-dd HH:mm:ss"));
            using var r = cmd.ExecuteReader();

            var rows = new List<(string Short, string Ext, string Name)>();
            while (r.Read())
                rows.Add((r.IsDBNull(0) ? "" : r.GetString(0),
                          r.IsDBNull(1) ? "" : r.GetString(1),
                          r.IsDBNull(2) ? "" : r.GetString(2)));
            r.Close();

            if (rows.Count == 0) return null;

            var best = rows[0];
            if (!string.IsNullOrEmpty(preferTitle) && rows.Count > 1)
            {
                // 同局名マルチサービス対策: タイトルの双方向包含でベスト候補を選ぶ
                var match = rows.FirstOrDefault(row =>
                    row.Name.Contains(preferTitle, StringComparison.OrdinalIgnoreCase) ||
                    preferTitle.Contains(row.Name, StringComparison.OrdinalIgnoreCase));
                if (match != default) best = match;
            }

            // ① ファイルタイトルとEPGイベント名が全く無関係なら返さない
            //    （同局名マルチサービスの誤マッチ対策）
            if (!string.IsNullOrEmpty(preferTitle))
            {
                if (string.IsNullOrEmpty(best.Name)) return null;
                if (!HasCommonTrigram(preferTitle, best.Name) &&
                    !HasCommonTrigram(best.Name, preferTitle))
                    return null;
            }

            if (string.IsNullOrEmpty(best.Short) && string.IsNullOrEmpty(best.Ext)) return null;

            // 説明文はタイトル文字列を含まないことが普通にある（あかね噺の説明に
            // 「あかね噺」が出てこない等）ため、event_name と説明文の照合はしない。
            // 2026-06 の SyncCacheToDbAsync 汚染データは DB 側でクリア済み。

            return string.IsNullOrEmpty(best.Ext)   ? best.Short
                 : string.IsNullOrEmpty(best.Short) ? best.Ext
                 : best.Short + "\n" + best.Ext;
        }
        catch { return null; }
    }

    public string? GetEventInfoText(int onid, int tsid, int sid, int eventId)
    {
        if (!IsConfigured) return null;
        try
        {
            using var conn = new MySqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT short_text, ext_text FROM events " +
                "WHERE onid=@o AND tsid=@t AND sid=@s AND event_id=@e LIMIT 1";
            cmd.Parameters.AddWithValue("@o", onid);
            cmd.Parameters.AddWithValue("@t", tsid);
            cmd.Parameters.AddWithValue("@s", sid);
            cmd.Parameters.AddWithValue("@e", eventId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var shortText = r.IsDBNull(0) ? "" : r.GetString(0);
            var extText   = r.IsDBNull(1) ? "" : r.GetString(1);
            if (string.IsNullOrEmpty(shortText) && string.IsNullOrEmpty(extText)) return null;
            return string.IsNullOrEmpty(extText)   ? shortText
                 : string.IsNullOrEmpty(shortText) ? extText
                 : shortText + "\n" + extText;
        }
        catch { return null; }
    }

    public string? LastSearchError { get; private set; }

    public List<EpgEvent> SearchEvents(string keyword, int limit = 200)
    {
        LastSearchError = null;
        if (!IsConfigured || string.IsNullOrWhiteSpace(keyword)) return [];
        var terms = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        try
        {
            using var conn = new MySqlConnection(_connStr);
            conn.Open();
            var cols = new[] { "e.event_name", "e.short_text", "e.ext_text" };
            const string select =
                "SELECT e.onid, e.tsid, e.sid, e.event_id, s.service_name, e.start_time, e.duration_sec, " +
                "e.event_name, e.short_text, e.ext_text, e.free_ca_flag " +
                "FROM events e JOIN services s ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid ";

            // REGEXP で語を [\\s　]* で繋いだフレーズパターン
            // "古賀 葵" → 古賀[\s　]*葵  ← 隣接のみ許容。別人名が並ぶキャスト列の誤ヒットを防ぐ
            var escaped = terms.Select(EscapeRegex).ToArray();
            var phrasePattern = string.Join("[\\s　]*", escaped);
            var phraseWhere = string.Join(" OR ", cols.Select(c => $"({c} REGEXP @pat)"));
            using var c1 = conn.CreateCommand();
            c1.CommandText = $"{select}WHERE ({phraseWhere}) ORDER BY e.start_time DESC LIMIT @lim";
            c1.Parameters.AddWithValue("@pat", phrasePattern);
            c1.Parameters.AddWithValue("@lim", limit);
            using var r1 = c1.ExecuteReader();
            var result = ReadEvents(r1);
            if (result.Count > 0) return result;

            // フォールバック: 同一フィールド内で全語が LIKE 一致（一般 AND 検索）
            var fieldAnd = cols.Select(c => "(" + string.Join(" AND ", terms.Select((_, i) => $"{c} LIKE @t{i}")) + ")");
            var andWhere = "(" + string.Join(" OR ", fieldAnd) + ")";
            using var c2 = conn.CreateCommand();
            c2.CommandText = $"{select}WHERE {andWhere} ORDER BY e.start_time DESC LIMIT @lim";
            for (int i = 0; i < terms.Length; i++)
                c2.Parameters.AddWithValue($"@t{i}", $"%{terms[i]}%");
            c2.Parameters.AddWithValue("@lim", limit);
            using var r2 = c2.ExecuteReader();
            return ReadEvents(r2);
        }
        catch (Exception ex) { LastSearchError = ex.Message; return []; }
    }

    /// <summary>
    /// 番組表用: 指定時間範囲の全TVサービスのイベントを取得する。
    /// 過去データも events テーブルに残っている分はすべて取得可能。
    /// 表示順は 地デジ→BS→CS、リモコンキー順。本文 (short/ext) は重いので
    /// 含めない（詳細表示時に GetEventInfoText で個別取得する）。
    /// </summary>
    public List<EpgEvent> GetGuideEvents(DateTime rangeStart, DateTime rangeEnd)
    {
        if (!IsConfigured) return [];
        try
        {
            using var conn = new MySqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT e.onid, e.tsid, e.sid, e.event_id, s.service_name, " +
                "e.start_time, e.duration_sec, e.event_name, e.free_ca_flag, g.nibble_l1 " +
                "FROM events e " +
                "JOIN services s ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid " +
                "LEFT JOIN event_genres g ON g.onid=e.onid AND g.tsid=e.tsid " +
                "AND g.sid=e.sid AND g.event_id=e.event_id AND g.seq=0 " +
                "WHERE e.start_time >= @lo AND e.start_time < @hi " +
                "AND s.service_type=1 AND s.partial_reception=0 " +
                "ORDER BY " +
                "CASE WHEN e.onid>=30848 THEN 0 WHEN e.onid=4 THEN 1 " +
                "WHEN e.onid IN (6,7) THEN 2 ELSE 3 END, " +
                "s.remote_control_key, e.onid, e.tsid, e.sid, e.start_time";
            cmd.Parameters.AddWithValue("@lo", rangeStart.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@hi", rangeEnd.ToString("yyyy-MM-dd HH:mm:ss"));
            using var r = cmd.ExecuteReader();
            var list = new List<EpgEvent>();
            while (r.Read())
                list.Add(new EpgEvent
                {
                    ONID          = (ushort)r.GetInt32(0),
                    TSID          = (ushort)r.GetInt32(1),
                    SID           = (ushort)r.GetInt32(2),
                    EventID       = (ushort)r.GetInt32(3),
                    ServiceName   = r.IsDBNull(4) ? "" : r.GetString(4),
                    StartTime     = r.IsDBNull(5) ? null : (DateTime?)r.GetDateTime(5),
                    DurationSec   = r.IsDBNull(6) ? null : (uint?)r.GetInt32(6),
                    EventName     = r.IsDBNull(7) ? "" : r.GetString(7),
                    FreeCAFlag    = (byte)(r.IsDBNull(8) ? 0 : r.GetInt32(8)),
                    ContentNibble = r.IsDBNull(9) ? null : (byte?)r.GetInt32(9),
                });
            return list;
        }
        catch { return []; }
    }

    // a の3文字部分列が b に1つでも含まれるか
    internal static bool HasCommonTrigram(string a, string b)
    {
        for (int i = 0; i + 2 < a.Length; i++)
            if (b.Contains(a.Substring(i, 3), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string EscapeRegex(string s) =>
        System.Text.RegularExpressions.Regex.Escape(s).Replace("/", "\\/");

    private static List<EpgEvent> ReadEvents(MySqlDataReader r)
    {
        var list = new List<EpgEvent>();
        while (r.Read())
            list.Add(new EpgEvent
            {
                ONID        = (ushort)r.GetInt32(0),
                TSID        = (ushort)r.GetInt32(1),
                SID         = (ushort)r.GetInt32(2),
                EventID     = (ushort)r.GetInt32(3),
                ServiceName = r.IsDBNull(4)  ? "" : r.GetString(4),
                StartTime   = r.IsDBNull(5)  ? null : (DateTime?)r.GetDateTime(5),
                DurationSec = r.IsDBNull(6)  ? null : (uint?)r.GetInt32(6),
                EventName   = r.IsDBNull(7)  ? "" : r.GetString(7),
                ShortText   = r.IsDBNull(8)  ? "" : r.GetString(8),
                ExtText     = r.IsDBNull(9)  ? "" : r.GetString(9),
                FreeCAFlag  = (byte)(r.IsDBNull(10) ? 0 : r.GetInt32(10)),
            });
        return list;
    }

}
