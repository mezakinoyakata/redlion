using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using EDCBViewer.Models;

namespace EDCBViewer.Services;

public sealed class RecordingIndex : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _conn;
    private readonly object _lock = new();

    public RecordingIndex(string path) => _dbPath = path;

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

    public void Load()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            _conn = new SqliteConnection($"Data Source={_dbPath}");
            _conn.Open();

            using var pragma = _conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();

            using var create = _conn.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS recordings (
                    file_name          TEXT PRIMARY KEY,
                    full_title         TEXT NOT NULL DEFAULT '',
                    series_title       TEXT NOT NULL DEFAULT '',
                    episode_number     INTEGER,
                    start_time         TEXT NOT NULL DEFAULT '',
                    start_time_epg     TEXT NOT NULL DEFAULT '',
                    duration_second    INTEGER NOT NULL DEFAULT 0,
                    service_name       TEXT NOT NULL DEFAULT '',
                    rec_id             INTEGER NOT NULL DEFAULT 0,
                    onid               INTEGER NOT NULL DEFAULT 0,
                    tsid               INTEGER NOT NULL DEFAULT 0,
                    sid                INTEGER NOT NULL DEFAULT 0,
                    event_id           INTEGER NOT NULL DEFAULT 0,
                    program_info       TEXT NOT NULL DEFAULT '',
                    comment            TEXT NOT NULL DEFAULT '',
                    err_info           TEXT NOT NULL DEFAULT '',
                    drops              INTEGER NOT NULL DEFAULT 0,
                    scrambles          INTEGER NOT NULL DEFAULT 0,
                    rec_status         INTEGER NOT NULL DEFAULT 0,
                    protect_flag       INTEGER NOT NULL DEFAULT 0,
                    original_file_path TEXT NOT NULL DEFAULT '',
                    saved_at           TEXT NOT NULL DEFAULT ''
                )";
            create.ExecuteNonQuery();

            // 既存DBへのカラム追加（既存なら無視）
            foreach (var col in (string[])[
                "start_time_epg TEXT NOT NULL DEFAULT ''",
                "rec_id INTEGER NOT NULL DEFAULT 0",
                "onid INTEGER NOT NULL DEFAULT 0",
                "tsid INTEGER NOT NULL DEFAULT 0",
                "sid INTEGER NOT NULL DEFAULT 0",
                "event_id INTEGER NOT NULL DEFAULT 0",
                "err_info TEXT NOT NULL DEFAULT ''",
                "protect_flag INTEGER NOT NULL DEFAULT 0",
            ])
            {
                try
                {
                    using var alter = _conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE recordings ADD COLUMN {col}";
                    alter.ExecuteNonQuery();
                }
                catch { }
            }

            MigrateFromJson();
        }
    }

    private void MigrateFromJson()
    {
        var jsonPath = Path.ChangeExtension(_dbPath, ".json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var list = JsonSerializer.Deserialize<List<RecordingIndexEntry>>(json);
            if (list == null || list.Count == 0) return;

            using var tx = _conn!.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO recordings
                (file_name, full_title, series_title, episode_number, start_time,
                 duration_second, service_name, program_info, comment,
                 drops, scrambles, rec_status, original_file_path, saved_at)
                VALUES ($fn,$ft,$st,$ep,$dt,$dur,$svc,$pi,$cmt,$drops,$scr,$rs,$fp,$sa)";

            var pFn  = cmd.Parameters.Add("$fn",    SqliteType.Text);
            var pFt  = cmd.Parameters.Add("$ft",    SqliteType.Text);
            var pSt  = cmd.Parameters.Add("$st",    SqliteType.Text);
            var pEp  = cmd.Parameters.Add("$ep",    SqliteType.Integer);
            var pDt  = cmd.Parameters.Add("$dt",    SqliteType.Text);
            var pDur = cmd.Parameters.Add("$dur",   SqliteType.Integer);
            var pSvc = cmd.Parameters.Add("$svc",   SqliteType.Text);
            var pPi  = cmd.Parameters.Add("$pi",    SqliteType.Text);
            var pCmt = cmd.Parameters.Add("$cmt",   SqliteType.Text);
            var pDr  = cmd.Parameters.Add("$drops", SqliteType.Integer);
            var pScr = cmd.Parameters.Add("$scr",   SqliteType.Integer);
            var pRs  = cmd.Parameters.Add("$rs",    SqliteType.Integer);
            var pFp  = cmd.Parameters.Add("$fp",    SqliteType.Text);
            var pSa  = cmd.Parameters.Add("$sa",    SqliteType.Text);

            foreach (var e in list)
            {
                var fn = Path.GetFileNameWithoutExtension(e.OriginalFilePath);
                if (string.IsNullOrEmpty(fn)) continue;

                pFn.Value  = fn;
                pFt.Value  = e.FullTitle;
                pSt.Value  = e.SeriesTitle;
                pEp.Value  = e.EpisodeNumber.HasValue ? (object)e.EpisodeNumber.Value : DBNull.Value;
                pDt.Value  = e.StartTime.ToString("o");
                pDur.Value = (long)e.DurationSecond;
                pSvc.Value = e.ServiceName;
                pPi.Value  = e.ProgramInfo ?? "";
                pCmt.Value = e.Comment ?? "";
                pDr.Value  = e.Drops;
                pScr.Value = e.Scrambles;
                pRs.Value  = (long)e.RecStatus;
                pFp.Value  = e.OriginalFilePath ?? "";
                pSa.Value  = e.SavedAt.ToString("o");
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            File.Move(jsonPath, jsonPath + ".migrated");
        }
        catch { }
    }

    public void AddOrUpdate(RecFileInfo rec, string programInfo)
    {
        var fileName = Path.GetFileNameWithoutExtension(rec.RecFilePath);
        if (string.IsNullOrEmpty(fileName)) return;

        var (series, episode) = ParseTitle(rec.Title);

        lock (_lock)
        {
            if (_conn == null) return;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO recordings
                (file_name, full_title, series_title, episode_number, start_time, start_time_epg,
                 duration_second, service_name, rec_id, onid, tsid, sid, event_id,
                 program_info, comment, err_info, drops, scrambles, rec_status, protect_flag,
                 original_file_path, saved_at)
                VALUES ($fn,$ft,$st,$ep,$dt,$dte,$dur,$svc,$rid,$onid,$tsid,$sid,$eid,
                        $pi,$cmt,$err,$drops,$scr,$rs,$pf,$fp,$sa)
                ON CONFLICT(file_name) DO UPDATE SET
                    full_title         = $ft,
                    series_title       = $st,
                    episode_number     = $ep,
                    start_time         = $dt,
                    start_time_epg     = $dte,
                    duration_second    = $dur,
                    service_name       = $svc,
                    rec_id             = $rid,
                    onid               = $onid,
                    tsid               = $tsid,
                    sid                = $sid,
                    event_id           = $eid,
                    program_info       = CASE WHEN $pi != '' THEN $pi ELSE program_info END,
                    comment            = $cmt,
                    err_info           = $err,
                    drops              = $drops,
                    scrambles          = $scr,
                    rec_status         = $rs,
                    protect_flag       = $pf,
                    original_file_path = $fp,
                    saved_at           = $sa";

            cmd.Parameters.AddWithValue("$fn",   fileName);
            cmd.Parameters.AddWithValue("$ft",   rec.Title ?? "");
            cmd.Parameters.AddWithValue("$st",   series);
            cmd.Parameters.AddWithValue("$ep",   episode.HasValue ? (object)episode.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$dt",   rec.StartTime.ToString("o"));
            cmd.Parameters.AddWithValue("$dte",  rec.StartTimeEpg.ToString("o"));
            cmd.Parameters.AddWithValue("$dur",  (long)rec.DurationSecond);
            cmd.Parameters.AddWithValue("$svc",  rec.ServiceName ?? "");
            cmd.Parameters.AddWithValue("$rid",  (long)rec.ID);
            cmd.Parameters.AddWithValue("$onid", (long)rec.OriginalNetworkID);
            cmd.Parameters.AddWithValue("$tsid", (long)rec.TransportStreamID);
            cmd.Parameters.AddWithValue("$sid",  (long)rec.ServiceID);
            cmd.Parameters.AddWithValue("$eid",  (long)rec.EventID);
            cmd.Parameters.AddWithValue("$pi",   programInfo ?? "");
            cmd.Parameters.AddWithValue("$cmt",  rec.Comment ?? "");
            cmd.Parameters.AddWithValue("$err",  rec.ErrInfo ?? "");
            cmd.Parameters.AddWithValue("$drops", rec.Drops);
            cmd.Parameters.AddWithValue("$scr",  rec.Scrambles);
            cmd.Parameters.AddWithValue("$rs",   (long)rec.RecStatus);
            cmd.Parameters.AddWithValue("$pf",   (long)rec.ProtectFlag);
            cmd.Parameters.AddWithValue("$fp",   rec.RecFilePath ?? "");
            cmd.Parameters.AddWithValue("$sa",   DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    // ファイル名（拡張子なし）でインデックスを検索する
    public RecordingIndexEntry? Find(string fileName)
    {
        lock (_lock)
        {
            if (_conn == null) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM recordings WHERE file_name = $fn LIMIT 1";
            cmd.Parameters.AddWithValue("$fn", fileName);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadEntry(r) : null;
        }
    }

    private static RecordingIndexEntry ReadEntry(SqliteDataReader r)
    {
        int Ord(string col) => r.GetOrdinal(col);
        static DateTime ParseDt(string s) => string.IsNullOrEmpty(s) ? default : DateTime.Parse(s);
        return new RecordingIndexEntry
        {
            FileName           = r.GetString(Ord("file_name")),
            FullTitle          = r.GetString(Ord("full_title")),
            SeriesTitle        = r.GetString(Ord("series_title")),
            EpisodeNumber      = r.IsDBNull(Ord("episode_number")) ? null : r.GetInt32(Ord("episode_number")),
            StartTime          = ParseDt(r.GetString(Ord("start_time"))),
            StartTimeEpg       = ParseDt(r.GetString(Ord("start_time_epg"))),
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
            SavedAt            = ParseDt(r.GetString(Ord("saved_at"))),
        };
    }

public void Save() { } // SQLiteは即時書き込みのためno-op

    public void Dispose()
    {
        lock (_lock)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }
}
