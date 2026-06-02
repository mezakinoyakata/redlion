using System.Text.RegularExpressions;
using MySqlConnector;
using EDCBViewer.Models;

namespace EDCBViewer.Services;

public sealed class RecordingIndex : IDisposable
{
    private readonly string _connStr;
    private readonly object _lock = new();

    public RecordingIndex(string connStr) => _connStr = connStr;

    // タイトルをシリーズ名と話数に分解する。
    // 対応パターン: " #N" "＃N" "第N話" "第N回" "（N）" "(N)" 末尾 " N"
    public static (string series, int? episode) ParseTitle(string title)
    {
        Match m;

        m = Regex.Match(title, @"[　 ][#＃](\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var ep))
            return (title[..m.Index].TrimEnd(' ', '　'), ep);

        m = Regex.Match(title, @"第(\d+)[話回]");
        if (m.Success && int.TryParse(m.Groups[1].Value, out ep))
            return (title[..m.Index].TrimEnd(' ', '　'), ep);

        m = Regex.Match(title, @"[（(](\d+)[）)]");
        if (m.Success && int.TryParse(m.Groups[1].Value, out ep))
            return (title[..m.Index].TrimEnd(' ', '　'), ep);

        m = Regex.Match(title, @"[　 ](\d{1,3})$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out ep))
            return (title[..m.Index].TrimEnd(' ', '　'), ep);

        return (title, null);
    }

    private MySqlConnection OpenConnection()
    {
        var conn = new MySqlConnection(_connStr);
        conn.Open();
        return conn;
    }

    public void Load()
    {
        if (string.IsNullOrWhiteSpace(_connStr)) return;
        // スキーマは移行スクリプトで作成済みのため確認のみ
        try { using var conn = OpenConnection(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RecordingIndex: MySQL 接続失敗: {ex.Message}");
        }
    }

    public void AddOrUpdate(RecFileInfo rec, string programInfo)
    {
        if (string.IsNullOrWhiteSpace(_connStr)) return;
        var fileName = Path.GetFileNameWithoutExtension(rec.RecFilePath);
        if (string.IsNullOrEmpty(fileName)) return;

        var (series, episode) = ParseTitle(rec.Title);

        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO recordings
                (file_name, full_title, series_title, episode_number, start_time, start_time_epg,
                 duration_second, service_name, rec_id, onid, tsid, sid, event_id,
                 program_info, comment, err_info, drops, scrambles, rec_status, protect_flag,
                 original_file_path, saved_at)
                VALUES (@fn,@ft,@st,@ep,@dt,@dte,@dur,@svc,@rid,@onid,@tsid,@sid,@eid,
                        @pi,@cmt,@err,@drops,@scr,@rs,@pf,@fp,@sa)
                ON DUPLICATE KEY UPDATE
                    full_title         = @ft,
                    series_title       = @st,
                    episode_number     = @ep,
                    start_time         = @dt,
                    start_time_epg     = @dte,
                    duration_second    = @dur,
                    service_name       = @svc,
                    rec_id             = @rid,
                    onid               = @onid,
                    tsid               = @tsid,
                    sid                = @sid,
                    event_id           = @eid,
                    program_info       = CASE WHEN @pi != '' THEN @pi ELSE program_info END,
                    comment            = @cmt,
                    err_info           = @err,
                    drops              = @drops,
                    scrambles          = @scr,
                    rec_status         = @rs,
                    protect_flag       = @pf,
                    original_file_path = @fp,
                    saved_at           = @sa";

            cmd.Parameters.AddWithValue("@fn",    fileName);
            cmd.Parameters.AddWithValue("@ft",    rec.Title ?? "");
            cmd.Parameters.AddWithValue("@st",    series);
            cmd.Parameters.AddWithValue("@ep",    episode.HasValue ? (object)episode.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@dt",    rec.StartTime == default ? DBNull.Value : (object)rec.StartTime);
            cmd.Parameters.AddWithValue("@dte",   rec.StartTimeEpg == default ? DBNull.Value : (object)rec.StartTimeEpg);
            cmd.Parameters.AddWithValue("@dur",   (long)rec.DurationSecond);
            cmd.Parameters.AddWithValue("@svc",   rec.ServiceName ?? "");
            cmd.Parameters.AddWithValue("@rid",   (long)rec.ID);
            cmd.Parameters.AddWithValue("@onid",  (long)rec.OriginalNetworkID);
            cmd.Parameters.AddWithValue("@tsid",  (long)rec.TransportStreamID);
            cmd.Parameters.AddWithValue("@sid",   (long)rec.ServiceID);
            cmd.Parameters.AddWithValue("@eid",   (long)rec.EventID);
            cmd.Parameters.AddWithValue("@pi",    programInfo ?? "");
            cmd.Parameters.AddWithValue("@cmt",   rec.Comment ?? "");
            cmd.Parameters.AddWithValue("@err",   rec.ErrInfo ?? "");
            cmd.Parameters.AddWithValue("@drops", rec.Drops);
            cmd.Parameters.AddWithValue("@scr",   rec.Scrambles);
            cmd.Parameters.AddWithValue("@rs",    (long)rec.RecStatus);
            cmd.Parameters.AddWithValue("@pf",    (long)rec.ProtectFlag);
            cmd.Parameters.AddWithValue("@fp",    rec.RecFilePath ?? "");
            cmd.Parameters.AddWithValue("@sa",    DateTime.Now);
            cmd.ExecuteNonQuery();
        }
    }

    public RecordingIndexEntry? Find(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_connStr)) return null;
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM recordings WHERE file_name = @fn LIMIT 1";
            cmd.Parameters.AddWithValue("@fn", fileName);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadEntry(r) : null;
        }
    }

    private static RecordingIndexEntry ReadEntry(MySqlDataReader r)
    {
        int Ord(string col) => r.GetOrdinal(col);
        static DateTime? GetDt(MySqlDataReader r, int i) =>
            r.IsDBNull(i) ? null : r.GetDateTime(i);
        return new RecordingIndexEntry
        {
            FileName           = r.GetString(Ord("file_name")),
            FullTitle          = r.GetString(Ord("full_title")),
            SeriesTitle        = r.GetString(Ord("series_title")),
            EpisodeNumber      = r.IsDBNull(Ord("episode_number")) ? null : r.GetInt32(Ord("episode_number")),
            StartTime          = GetDt(r, Ord("start_time")) ?? default,
            StartTimeEpg       = GetDt(r, Ord("start_time_epg")) ?? default,
            DurationSecond     = (uint)r.GetInt64(Ord("duration_second")),
            ServiceName        = r.GetString(Ord("service_name")),
            RecId              = (uint)r.GetInt64(Ord("rec_id")),
            OriginalNetworkID  = (ushort)r.GetInt64(Ord("onid")),
            TransportStreamID  = (ushort)r.GetInt64(Ord("tsid")),
            ServiceID          = (ushort)r.GetInt64(Ord("sid")),
            EventID            = (ushort)r.GetInt64(Ord("event_id")),
            ProgramInfo        = r.GetString(Ord("program_info")),
            Comment            = r.GetString(Ord("comment")),
            ErrInfo            = r.GetString(Ord("err_info")),
            Drops              = r.GetInt64(Ord("drops")),
            Scrambles          = r.GetInt64(Ord("scrambles")),
            RecStatus          = (uint)r.GetInt64(Ord("rec_status")),
            ProtectFlag        = (byte)r.GetInt64(Ord("protect_flag")),
            OriginalFilePath   = r.GetString(Ord("original_file_path")),
            SavedAt            = GetDt(r, Ord("saved_at")) ?? default,
        };
    }

    public void Save() { }

    public void Dispose() { }
}
