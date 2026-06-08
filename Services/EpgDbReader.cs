using MySqlConnector;
using EDCBViewer.Models;

namespace EDCBViewer.Services;

public sealed class EpgDbReader
{
    private readonly string _connStr;

    public EpgDbReader(string connStr) => _connStr = connStr;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connStr);

    public string? GetEventInfoTextByStationAndTime(string stationName, DateTime startTime)
    {
        if (!IsConfigured || string.IsNullOrEmpty(stationName)) return null;
        try
        {
            using var conn = new MySqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT e.short_text, e.ext_text FROM events e " +
                "JOIN services s ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid " +
                "WHERE s.service_name=@svc AND e.start_time >= @lo AND e.start_time <= @hi LIMIT 1";
            cmd.Parameters.AddWithValue("@svc", stationName);
            cmd.Parameters.AddWithValue("@lo", startTime.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@hi", startTime.AddMinutes(2).ToString("yyyy-MM-dd HH:mm:ss"));
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

    // キャッシュ済みの番組情報を MySQL に INSERT IGNORE で書き込む（既存行は上書きしない）
    public async Task SyncCacheToDbAsync(
        IReadOnlyList<RecFileInfo> recordings,
        IReadOnlyDictionary<uint, string> cache)
    {
        if (!IsConfigured) return;
        var targets = recordings
            .Where(r => r.EventID != 0
                     && cache.TryGetValue(r.ID, out var t)
                     && !string.IsNullOrEmpty(t))
            .ToList();
        if (targets.Count == 0) return;

        using var conn = new MySqlConnection(_connStr);
        await conn.OpenAsync();

        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        const int batch = 500;
        for (int i = 0; i < targets.Count; i += batch)
        {
            using var tx = await conn.BeginTransactionAsync();
            foreach (var rec in targets.Skip(i).Take(batch))
            {
                cache.TryGetValue(rec.ID, out var text);
                var yw = rec.StartTime.Year * 100 + (rec.StartTime.DayOfYear - 1) / 7 + 1;
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var startTimeStr = rec.StartTime == DateTime.MinValue
                    ? null : (string?)rec.StartTime.ToString("yyyy-MM-ddTHH:mm:ss");
                cmd.CommandText =
                    "INSERT INTO events" +
                    "(onid,tsid,sid,event_id,start_time,event_name,short_text,ext_text," +
                    "free_ca_flag,updated_at,year_week,reserve_status)" +
                    "VALUES(@o,@t,@s,@e,@st,@name,'',@text,0,@now,@yw,2)" +
                    " ON DUPLICATE KEY UPDATE" +
                    " start_time=IF(start_time IS NULL,VALUES(start_time),start_time)," +
                    " reserve_status=IF(reserve_status<2,2,reserve_status)";
                cmd.Parameters.AddWithValue("@o",    rec.OriginalNetworkID);
                cmd.Parameters.AddWithValue("@t",    rec.TransportStreamID);
                cmd.Parameters.AddWithValue("@s",    rec.ServiceID);
                cmd.Parameters.AddWithValue("@e",    rec.EventID);
                cmd.Parameters.AddWithValue("@st",   (object?)startTimeStr ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@name", rec.Title);
                cmd.Parameters.AddWithValue("@text", text ?? "");
                cmd.Parameters.AddWithValue("@now",  now);
                cmd.Parameters.AddWithValue("@yw",   yw);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
    }
}
