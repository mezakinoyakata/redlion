using Npgsql;

namespace EDCBViewer.Services;

/// <summary>
/// EDCB の EPG 蓄積ファイル(*_epg.dat)を読んで PostgreSQL (Supabase) に取り込む。
///
/// EpgTimerSrv には触らない。EpgDataCap3.dll にファイルを食わせるだけなので、
/// EDCB のプロセスを起動する必要も、録画機の設定を変える必要もない。
///
/// 43 万件規模を 1 件ずつ INSERT すると現実的な時間で終わらないため、
/// 一時テーブルへ COPY (バイナリ) で流し込み、そこから 1 文の UPSERT でまとめて反映する。
/// </summary>
public sealed class EpgImporter : IDisposable
{
    private readonly NpgsqlConnection _conn;

    public EpgImporter(string connectionString)
    {
        _conn = new NpgsqlConnection(connectionString);
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    public sealed record ImportResult(bool Ok, string Message);

    /// <summary>
    /// dir 内の *_epg.dat を読んで DB に反映する。UI スレッドから呼ばないこと。
    /// 例外は投げず、失敗は Ok=false で返す（EPG が取り込めなくても、
    /// 今 DB にあるデータで一覧は出せる）。
    /// </summary>
    public static ImportResult Run(string connectionString, string dir, Action<string>? progress = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return new(false, "DB が未設定です");
            if (string.IsNullOrWhiteSpace(dir))              return new(false, "EPGデータフォルダが未設定です");
            if (!Directory.Exists(dir))                      return new(false, $"EPGデータフォルダがありません: {dir}");

            var files = Directory.GetFiles(dir, "*_epg.dat");
            if (files.Length == 0) return new(false, $"*_epg.dat がありません: {dir}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            progress?.Invoke($"EPG読み込み中… ({files.Length} ファイル)");

            List<EpgService> services;
            var events = new List<EpgEventRow>();
            using (var epg = new EpgDataCap3())
            {
                // 1 ファイルでも読めなければ取り込み全体を失敗にする。
                // 一部だけ取り込んで「完了」と出すと、欠けに気付けないため。
                foreach (var f in files) epg.LoadFile(f);
                services = epg.GetServices();
                foreach (var s in services) events.AddRange(epg.GetEvents(s));
            }

            progress?.Invoke($"EPG書き込み中… ({events.Count} 件)");
            using var db = new EpgImporter(connectionString);
            db.WriteServices(services, DateTime.Now);
            var (ne, _) = db.WriteEvents(events, DateTime.Now);

            return new(true, $"EPG取り込み完了: {ne} 件 ({sw.Elapsed.TotalSeconds:F0} 秒)");
        }
        catch (Exception ex)
        {
            return new(false, "EPG取り込み失敗: " + ex.Message);
        }
    }

    private void Exec(string sql)
    {
        using var cmd = new NpgsqlCommand(sql, _conn);
        cmd.ExecuteNonQuery();
    }

    // ─── services ────────────────────────────────────────────────────────────

    public int WriteServices(IReadOnlyCollection<EpgService> services, DateTime now)
    {
        if (services.Count == 0) return 0;

        Exec("""
            CREATE TEMP TABLE tmp_services (LIKE services INCLUDING DEFAULTS)
            ON COMMIT DROP
            """);

        using (var tran = _conn.BeginTransaction())
        {
            Exec("""
                CREATE TEMP TABLE IF NOT EXISTS tmp_services (LIKE services INCLUDING DEFAULTS)
                """);
            using (var writer = _conn.BeginBinaryImport(
                "COPY tmp_services (onid,tsid,sid,service_type,partial_reception," +
                "provider_name,service_name,network_name,ts_name,remote_control_key,updated_at) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var s in services)
                {
                    writer.StartRow();
                    writer.Write((int)s.Onid);
                    writer.Write((int)s.Tsid);
                    writer.Write((int)s.Sid);
                    writer.Write((short)s.ServiceType);
                    writer.Write((short)s.PartialReception);
                    writer.Write(s.ProviderName ?? "");
                    writer.Write(s.ServiceName ?? "");
                    writer.Write(s.NetworkName ?? "");
                    writer.Write(s.TsName ?? "");
                    writer.Write((short)s.RemoteControlKey);
                    writer.Write(now);
                }
                writer.Complete();
            }

            Exec("""
                INSERT INTO services
                SELECT * FROM tmp_services
                ON CONFLICT (onid,tsid,sid) DO UPDATE SET
                    service_type       = EXCLUDED.service_type,
                    partial_reception  = EXCLUDED.partial_reception,
                    provider_name      = EXCLUDED.provider_name,
                    service_name       = EXCLUDED.service_name,
                    network_name       = EXCLUDED.network_name,
                    ts_name            = EXCLUDED.ts_name,
                    remote_control_key = EXCLUDED.remote_control_key,
                    updated_at         = EXCLUDED.updated_at
                """);
            tran.Commit();
        }
        return services.Count;
    }

    // ─── events / event_genres ───────────────────────────────────────────────

    /// <summary>
    /// events と event_genres を反映する。
    ///
    /// reserve_status は 0=通常 / 3=未録画のまま終了 のみを立てる。
    /// 1(予約あり)・2(録画終了)は EpgTimerSrv に問い合わせないと分からないが、
    /// 本ツールは *_epg.dat しか読まないので判定しない。
    /// 既存行の 2 は UPSERT 側で保護する(下の CASE)。
    /// </summary>
    public (int events, int genres) WriteEvents(
        IReadOnlyCollection<EpgEventRow> events,
        DateTime now)
    {
        if (events.Count == 0) return (0, 0);

        int genreCount = 0;
        using var tran = _conn.BeginTransaction();

        Exec("CREATE TEMP TABLE tmp_events (LIKE events INCLUDING DEFAULTS)");
        Exec("CREATE TEMP TABLE tmp_genres (LIKE event_genres INCLUDING DEFAULTS)");

        using (var writer = _conn.BeginBinaryImport(
            "COPY tmp_events (onid,tsid,sid,event_id,start_time,duration_sec," +
            "event_name,short_text,ext_text," +
            "component_stream_content,component_type,component_tag,component_text," +
            "free_ca_flag,updated_at,year_week,reserve_status) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var e in events)
            {
                // 終了済みの番組(録画したかどうかは分からない)
                int rstat = e.StartTime is { } st && e.DurationSec is { } dur
                            && st.AddSeconds(dur) < DateTime.Now ? 3 : 0;

                writer.StartRow();
                writer.Write((int)e.Onid);
                writer.Write((int)e.Tsid);
                writer.Write((int)e.Sid);
                writer.Write((int)e.EventId);
                if (e.StartTime   is { } t) writer.Write(t);         else writer.WriteNull();
                if (e.DurationSec is { } d) writer.Write((int)d);    else writer.WriteNull();
                writer.Write(Trim(e.EventName, 512));
                writer.Write(e.ShortText ?? "");
                writer.Write(e.ExtText ?? "");
                if (e.ComponentStreamContent is { } cs) writer.Write((short)cs); else writer.WriteNull();
                if (e.ComponentType          is { } ct) writer.Write((short)ct); else writer.WriteNull();
                if (e.ComponentTag           is { } cg) writer.Write((short)cg); else writer.WriteNull();
                writer.Write(Trim(e.ComponentText, 256));
                writer.Write((short)e.FreeCaFlag);
                writer.Write(now);
                writer.Write(e.StartTime is { } yw ? CalcYearWeek(yw) : 0);
                writer.Write((short)rstat);
            }
            writer.Complete();
        }

        using (var writer = _conn.BeginBinaryImport(
            "COPY tmp_genres (onid,tsid,sid,event_id,seq,nibble_l1,nibble_l2,user_nibble_1,user_nibble_2) " +
            "FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var e in events)
            {
                if (e.Genres is not { Count: > 0 }) continue;
                short seq = 0;
                foreach (var g in e.Genres)
                {
                    writer.StartRow();
                    writer.Write((int)e.Onid);
                    writer.Write((int)e.Tsid);
                    writer.Write((int)e.Sid);
                    writer.Write((int)e.EventId);
                    writer.Write(seq++);
                    writer.Write((short)g.L1);
                    writer.Write((short)g.L2);
                    writer.Write((short)g.User1);
                    writer.Write((short)g.User2);
                    genreCount++;
                }
            }
            writer.Complete();
        }

        // ext_text: EPG 側が空でも既存値(録画情報から復元済み等)を消さない
        // reserve_status: 2(録画終了)は上書きしない
        Exec("""
            INSERT INTO events
            SELECT * FROM tmp_events
            ON CONFLICT (onid,tsid,sid,event_id) DO UPDATE SET
                start_time               = EXCLUDED.start_time,
                duration_sec             = EXCLUDED.duration_sec,
                event_name               = EXCLUDED.event_name,
                short_text               = EXCLUDED.short_text,
                ext_text                 = CASE WHEN EXCLUDED.ext_text = '' THEN events.ext_text ELSE EXCLUDED.ext_text END,
                component_stream_content = EXCLUDED.component_stream_content,
                component_type           = EXCLUDED.component_type,
                component_tag            = EXCLUDED.component_tag,
                component_text           = EXCLUDED.component_text,
                free_ca_flag             = EXCLUDED.free_ca_flag,
                updated_at               = EXCLUDED.updated_at,
                year_week                = EXCLUDED.year_week,
                reserve_status           = CASE WHEN events.reserve_status = 2 THEN 2 ELSE EXCLUDED.reserve_status END
            """);

        Exec("""
            INSERT INTO event_genres
            SELECT * FROM tmp_genres
            ON CONFLICT (onid,tsid,sid,event_id,seq) DO UPDATE SET
                nibble_l1     = EXCLUDED.nibble_l1,
                nibble_l2     = EXCLUDED.nibble_l2,
                user_nibble_1 = EXCLUDED.user_nibble_1,
                user_nibble_2 = EXCLUDED.user_nibble_2
            """);

        Exec("DROP TABLE tmp_events");
        Exec("DROP TABLE tmp_genres");
        tran.Commit();

        return (events.Count, genreCount);
    }

    // ─── ヘルパー ────────────────────────────────────────────────────────────

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max);
    }

    /// <summary>yyyyww。既存データ(MySQL 版 EpgSqliteExporter)と同じ値になるようにする。</summary>
    public static int CalcYearWeek(DateTime t)
    {
        var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        int week = cal.GetWeekOfYear(t, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        int year = t.Year;
        if (week >= 52 && t.Month == 1) year--;
        else if (week == 1 && t.Month == 12) year++;
        return year * 100 + week;
    }
}
