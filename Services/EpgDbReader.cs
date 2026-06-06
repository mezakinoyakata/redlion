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
            cmd.Parameters.AddWithValue("@lo", startTime.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ss"));
            cmd.Parameters.AddWithValue("@hi", startTime.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss"));
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
