using Npgsql;
using EDCBViewer.Models;

namespace EDCBViewer.Services;

public sealed class EpgDbReader
{
    private readonly string _connStr;

    public EpgDbReader(string connStr) => _connStr = connStr;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connStr);

    /// <summary>放送局名＋開始時刻で特定したイベントの正式タイトルと説明文。</summary>
    /// <remarks>EventName はファイル名では Title2 マクロで除去される [4K][HDR][字] 等の
    /// タグを含む EPG 側の正式タイトル。InfoText は説明文が無いイベントでは null。</remarks>
    public sealed record EventDisplayInfo(string EventName, string? InfoText);

    public EventDisplayInfo? GetEventInfoByStationAndTime(
        string stationName, DateTime startTime, string? preferTitle = null)
    {
        if (!IsConfigured || string.IsNullOrEmpty(stationName)) return null;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
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
            cmd.Parameters.AddWithValue("@lo", startTime.AddMinutes(-2));
            cmd.Parameters.AddWithValue("@hi", startTime.AddMinutes(2));
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

            // 説明文はタイトル文字列を含まないことが普通にある（あかね噺の説明に
            // 「あかね噺」が出てこない等）ため、event_name と説明文の照合はしない。
            // 2026-06 の SyncCacheToDbAsync 汚染データは DB 側でクリア済み。

            var infoText =
                  string.IsNullOrEmpty(best.Short) && string.IsNullOrEmpty(best.Ext) ? null
                : string.IsNullOrEmpty(best.Ext)   ? best.Short
                : string.IsNullOrEmpty(best.Short) ? best.Ext
                : best.Short + "\n" + best.Ext;
            return new EventDisplayInfo(best.Name, infoText);
        }
        catch { return null; }
    }

    public string? GetEventInfoText(int onid, int tsid, int sid, int eventId)
    {
        if (!IsConfigured) return null;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
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
            using var conn = new NpgsqlConnection(_connStr);
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
            var phraseWhere = string.Join(" OR ", cols.Select(c => $"({c} ~ @pat)"));
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
    /// 絞込検索用: 全語（AND）が番組名・説明文のいずれかのフィールドに LIKE 一致する
    /// イベントの (サービス名, 開始時刻) を返す。
    /// ファイル名には EDCB の Title2 マクロで [4K][HDR][字] 等のタグが除去されているため、
    /// EPG 側の情報でファイルを対応付けるために使う（対応付けは呼び出し側で行う）。
    /// DB照合順序は utf8mb4_0900_ai_ci なので全角半角・大文字小文字は無視される。
    /// </summary>
    public List<(string ServiceName, DateTime StartTime)> GetMatchingEventKeys(string[] terms)
    {
        if (!IsConfigured || terms.Length == 0) return [];
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            var cols = new[] { "e.event_name", "e.short_text", "e.ext_text" };
            var fieldAnd = cols.Select(c =>
                "(" + string.Join(" AND ", terms.Select((_, i) => $"{c} LIKE @t{i}")) + ")");
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT s.service_name, e.start_time " +
                "FROM events e JOIN services s ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid " +
                "WHERE (" + string.Join(" OR ", fieldAnd) + ")";
            for (int i = 0; i < terms.Length; i++)
                cmd.Parameters.AddWithValue($"@t{i}", $"%{terms[i]}%");
            using var r = cmd.ExecuteReader();
            var list = new List<(string, DateTime)>();
            while (r.Read())
                if (!r.IsDBNull(0) && !r.IsDBNull(1))
                    list.Add((r.GetString(0), r.GetDateTime(1)));
            return list;
        }
        catch { return []; }
    }

    /// <summary>
    /// 番組表用: 指定時間範囲の全TVサービスのイベントを取得する。
    /// 過去データも events テーブルに残っている分はすべて取得可能。
    /// 表示順は 地デジ→BS→CS、リモコンキー順。本文 (short/ext) は重いので
    /// 含めない（詳細表示時に GetEventInfoText で個別取得する）。
    /// </summary>
    public List<EpgEvent> GetGuideEvents(DateTime rangeStart, DateTime rangeEnd)
    {
        LastGuideError = null;
        if (!IsConfigured) { LastGuideError = "DB が未設定です"; return []; }
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
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
            cmd.Parameters.AddWithValue("@lo", rangeStart);
            cmd.Parameters.AddWithValue("@hi", rangeEnd);
            using var r = cmd.ExecuteReader();
            var list = new List<EpgEvent>();
            while (r.Read())
                list.Add(new EpgEvent
                {
                    ONID          = (ushort)r.GetInt32(0),
                    TSID          = (ushort)r.GetInt32(1),
                    SID           = (ushort)r.GetInt32(2),
                    EventID       = r.GetInt32(3),
                    ServiceName   = r.IsDBNull(4) ? "" : r.GetString(4),
                    StartTime     = r.IsDBNull(5) ? null : (DateTime?)r.GetDateTime(5),
                    DurationSec   = r.IsDBNull(6) ? null : (uint?)r.GetInt32(6),
                    EventName     = r.IsDBNull(7) ? "" : r.GetString(7),
                    FreeCAFlag    = (byte)(r.IsDBNull(8) ? 0 : r.GetInt32(8)),
                    ContentNibble = r.IsDBNull(9) ? null : (byte?)r.GetInt32(9),
                });
            return list;
        }
        catch (Exception ex) { LastGuideError = ex.Message; return []; }
    }

    /// <summary>番組表取得の直近の失敗理由（成功時は null）。</summary>
    /// <remarks>
    /// 失敗しても空リストを返すため、そのままでは「番組が無い日」と区別が付かない。
    /// DB が落ちている・型が合わない等の理由を画面に出せるようにここに残す。
    /// </remarks>
    public string? LastGuideError { get; private set; }

    // ─── しょぼカル連携（最速放送判定）─────────────────────────────────────
    // events テーブルは一切変更しない（列追加・書き込みなし）。
    // しょぼカルの生データは専用テーブル（syobocal_*）に持ち、読み取り時に
    // events と JOIN して最速を判定する。syobocal_* は FULLTEXT を持たないため
    // CREATE TABLE / INSERT / DELETE はすべて高速（events の ALTER で見つかった
    // 「FULLTEXTインデックス保持テーブルは INSTANT/INPLACE 変更不可、COPY必須で
    // 数分以上かかる」という制約を回避できる）。

    /// <summary>しょぼカル関連メソッドの直近の失敗理由（成功時は null）。</summary>
    public string? LastSyobocalError { get; private set; }

    /// <summary>syobocal_* テーブルが無ければ作成する。全て FULLTEXT なしなので高速。</summary>
    public bool EnsureSyobocalTables()
    {
        LastSyobocalError = null;
        if (!IsConfigured) return false;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS syobocal_airings (
                    pid INT NOT NULL PRIMARY KEY,
                    tid INT NOT NULL,
                    cnt INT NULL,
                    chid INT NOT NULL,
                    st_time TIMESTAMP NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_tid_cnt_st ON syobocal_airings (tid, cnt, st_time);
                CREATE INDEX IF NOT EXISTS idx_chid_st ON syobocal_airings (chid, st_time);
                CREATE TABLE IF NOT EXISTS syobocal_service_map (
                    service_name VARCHAR(255) NOT NULL,
                    chid INT NOT NULL,
                    PRIMARY KEY (service_name, chid)
                );
                CREATE TABLE IF NOT EXISTS syobocal_titles (
                    tid INT NOT NULL PRIMARY KEY,
                    first_ym INT NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS syobocal_meta (
                    k VARCHAR(64) NOT NULL PRIMARY KEY,
                    v VARCHAR(255) NOT NULL
                );
                ALTER TABLE syobocal_airings ADD COLUMN IF NOT EXISTS ed_time TIMESTAMP NULL;
                ALTER TABLE syobocal_airings ADD COLUMN IF NOT EXISTS sub_title VARCHAR(512) NOT NULL DEFAULT '';
                ALTER TABLE syobocal_titles  ADD COLUMN IF NOT EXISTS title VARCHAR(512) NOT NULL DEFAULT '';
                ALTER TABLE syobocal_titles  ADD COLUMN IF NOT EXISTS comment TEXT NOT NULL DEFAULT '';
                """;
            foreach (var stmt in cmd.CommandText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var c = conn.CreateCommand();
                c.CommandText = stmt;
                c.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return false; }
    }

    /// <summary>同期の進捗（カバー済み年月範囲・直近再取得時刻）。テーブル未作成/未同期時は全て 0/null。</summary>
    public (int CoveredFromYm, int CoveredToYm, DateTime? LastRecentRefresh) GetSyobocalMeta()
    {
        if (!IsConfigured) return (0, 0, null);
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT k, v FROM syobocal_meta WHERE k IN ('covered_from_ym','covered_to_ym','last_recent_refresh')";
            using var r = cmd.ExecuteReader();
            var dict = new Dictionary<string, string>();
            while (r.Read()) dict[r.GetString(0)] = r.GetString(1);
            var from = dict.TryGetValue("covered_from_ym", out var f) && int.TryParse(f, out var fi) ? fi : 0;
            var to   = dict.TryGetValue("covered_to_ym", out var t) && int.TryParse(t, out var ti) ? ti : 0;
            var last = dict.TryGetValue("last_recent_refresh", out var l) && DateTime.TryParse(l, out var ld) ? (DateTime?)ld : null;
            return (from, to, last);
        }
        catch { return (0, 0, null); }
    }

    public bool SetSyobocalMeta(int coveredFromYm, int coveredToYm, DateTime? lastRecentRefresh)
    {
        if (!IsConfigured) return false;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            var sql = "INSERT INTO syobocal_meta (k,v) VALUES " +
                      "('covered_from_ym',@f), ('covered_to_ym',@t)" +
                      (lastRecentRefresh.HasValue ? ", ('last_recent_refresh',@l)" : "") +
                      " ON CONFLICT (k) DO UPDATE SET v=EXCLUDED.v";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@f", coveredFromYm.ToString());
            cmd.Parameters.AddWithValue("@t", coveredToYm.ToString());
            if (lastRecentRefresh.HasValue)
                cmd.Parameters.AddWithValue("@l", lastRecentRefresh.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return false; }
    }

    /// <summary>指定期間の syobocal_airings を削除し、新しい行群で置き換える（同期の1チャンク分）。</summary>
    public bool ReplaceAiringsInRange(
        DateTime rangeLo, DateTime rangeHi,
        IReadOnlyCollection<(int Pid, int Tid, int? Cnt, int ChId, DateTime StTime,
                             DateTime? EdTime, string SubTitle)> rows)
    {
        LastSyobocalError = null;
        if (!IsConfigured) return false;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM syobocal_airings WHERE st_time >= @lo AND st_time < @hi";
                del.Parameters.AddWithValue("@lo", rangeLo);
                del.Parameters.AddWithValue("@hi", rangeHi);
                del.ExecuteNonQuery();
            }
            foreach (var chunk in rows.Chunk(500))
            {
                using var ins = conn.CreateCommand();
                var values = new List<string>();
                int i = 0;
                foreach (var (pid, tid, cnt, chid, st, ed, sub) in chunk)
                {
                    values.Add($"(@p{i},@t{i},@c{i},@h{i},@s{i},@e{i},@b{i})");
                    ins.Parameters.AddWithValue($"@p{i}", pid);
                    ins.Parameters.AddWithValue($"@t{i}", tid);
                    if (cnt.HasValue) ins.Parameters.AddWithValue($"@c{i}", cnt.Value);
                    else ins.Parameters.AddWithValue($"@c{i}", DBNull.Value);
                    ins.Parameters.AddWithValue($"@h{i}", chid);
                    ins.Parameters.AddWithValue($"@s{i}", st);
                    if (ed.HasValue) ins.Parameters.AddWithValue($"@e{i}", ed.Value);
                    else ins.Parameters.AddWithValue($"@e{i}", DBNull.Value);
                    ins.Parameters.AddWithValue($"@b{i}", sub ?? "");
                    i++;
                }
                if (values.Count == 0) continue;
                ins.CommandText =
                    "INSERT INTO syobocal_airings (pid,tid,cnt,chid,st_time,ed_time,sub_title) VALUES " +
                    string.Join(",", values) +
                    " ON CONFLICT (pid) DO UPDATE SET tid=EXCLUDED.tid, cnt=EXCLUDED.cnt," +
                    " chid=EXCLUDED.chid, st_time=EXCLUDED.st_time," +
                    " ed_time=EXCLUDED.ed_time, sub_title=EXCLUDED.sub_title";
                ins.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return false; }
    }

    /// <summary>EDCBサービス名 → しょぼカルChID の対応を差し替える。</summary>
    public bool ReplaceServiceMap(Dictionary<string, List<int>> map)
    {
        if (!IsConfigured) return false;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            foreach (var kv in map)
            {
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM syobocal_service_map WHERE service_name=@n";
                    del.Parameters.AddWithValue("@n", kv.Key);
                    del.ExecuteNonQuery();
                }
                if (kv.Value.Count == 0) continue;
                using var ins = conn.CreateCommand();
                var values = new List<string>();
                int i = 0;
                foreach (var chid in kv.Value)
                {
                    values.Add($"(@n,@c{i})");
                    ins.Parameters.AddWithValue($"@c{i}", chid);
                    i++;
                }
                ins.Parameters.AddWithValue("@n", kv.Key);
                ins.CommandText = "INSERT INTO syobocal_service_map (service_name, chid) VALUES " +
                                  string.Join(",", values) +
                                  " ON CONFLICT DO NOTHING";
                ins.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return false; }
    }

    public bool UpsertTitleFirstYm(Dictionary<int, (int FirstYm, string Title, string Comment)> dict)
    {
        if (!IsConfigured || dict.Count == 0) return false;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            foreach (var chunk in dict.Chunk(500))
            {
                using var ins = conn.CreateCommand();
                var values = new List<string>();
                int i = 0;
                foreach (var kv in chunk)
                {
                    values.Add($"(@t{i},@y{i},@n{i},@c{i})");
                    ins.Parameters.AddWithValue($"@t{i}", kv.Key);
                    ins.Parameters.AddWithValue($"@y{i}", kv.Value.FirstYm);
                    ins.Parameters.AddWithValue($"@n{i}", kv.Value.Title ?? "");
                    ins.Parameters.AddWithValue($"@c{i}", kv.Value.Comment ?? "");
                    i++;
                }
                // 作品名・解説は後から取れることがあるので、空で上書きしない
                ins.CommandText = "INSERT INTO syobocal_titles (tid, first_ym, title, comment) VALUES " +
                                  string.Join(",", values) +
                                  " ON CONFLICT (tid) DO UPDATE SET first_ym=EXCLUDED.first_ym," +
                                  " title=CASE WHEN EXCLUDED.title='' THEN syobocal_titles.title ELSE EXCLUDED.title END," +
                                  " comment=CASE WHEN EXCLUDED.comment='' THEN syobocal_titles.comment ELSE EXCLUDED.comment END";
                ins.ExecuteNonQuery();
            }
            return true;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return false; }
    }

    /// <summary>syobocal_titles に無い TID を返す（TitleLookup で補充すべき対象）。</summary>
    public List<int> GetMissingTitleIds(IReadOnlyCollection<int> tids)
    {
        if (!IsConfigured || tids.Count == 0) return [];
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tid FROM syobocal_titles WHERE tid IN (" +
                              string.Join(",", tids) + ")";
            using var r = cmd.ExecuteReader();
            var known = new HashSet<int>();
            while (r.Read()) known.Add(r.GetInt32(0));
            return tids.Where(t => !known.Contains(t)).ToList();
        }
        catch { return tids.ToList(); }
    }

    /// <summary>
    /// 最速放送の (サービス名, events.start_time[分精度]) を events との JOIN で求める。
    /// 話数のない放送・作品の放送開始年月がカバー範囲より前のものは対象外。
    /// events に対応する行が無い（=録画DBに存在しない）放送も対象外。
    /// </summary>
    public HashSet<(string ServiceName, DateTime StartTime)>? GetFastestKeysViaJoin(
        DateTime lo, DateTime hi, int coveredFromYm,
        IReadOnlyCollection<(string Station, DateTime Time)>? fileKeys = null)
    {
        if (!IsConfigured) return null;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();

            // 手持ちの録画ファイル分だけに絞る。これが無いと events を局単位で全走査するため、
            // 実測で 1,400 万行読んで 2 千行しか使わない状態になる（2026-09-02 EXPLAIN ANALYZE）。
            var useFileKeys = fileKeys is { Count: > 0 };
            if (useFileKeys) CreateFileKeyTempTable(conn, fileKeys!);

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 60;
            // events との対応は「±5分以内で最も近い1件」に限定する。単純な BETWEEN だと、
            // 同じ局で数分差の別番組が同時に存在する場合（実例: MX 01:00 の別アニメと
            // 01:05 のアズールレーンが両方 01:00 の syobocal 行にマッチしてしまった）、
            // 1つの syobocal 行が複数の events 行を誤って道連れにし、無関係な放送枠まで
            // 最速扱いになるバグが起きる（2026-07-20 実データで発見）。
            cmd.CommandText = $"""
                SELECT DISTINCT s.service_name, e.start_time
                FROM syobocal_airings a
                JOIN syobocal_service_map sm ON sm.chid = a.chid
                JOIN services s ON s.service_name = sm.service_name
                {(useFileKeys ? """
                JOIN tmp_file_keys fk ON fk.service_name = s.service_name
                JOIN events e ON e.start_time >= fk.start_time
                    AND e.start_time < fk.start_time + INTERVAL '1 minute'
                    AND e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid
                    AND e.start_time = (
                        SELECT e2.start_time FROM events e2
                        WHERE e2.onid=s.onid AND e2.tsid=s.tsid AND e2.sid=s.sid
                          AND e2.start_time BETWEEN a.st_time - INTERVAL '5 minutes'
                                                 AND a.st_time + INTERVAL '5 minutes'
                        ORDER BY ABS(EXTRACT(EPOCH FROM (e2.start_time - a.st_time)))
                        LIMIT 1
                    )
                """ : """
                JOIN events e ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid
                    AND e.start_time = (
                        SELECT e2.start_time FROM events e2
                        WHERE e2.onid=s.onid AND e2.tsid=s.tsid AND e2.sid=s.sid
                          AND e2.start_time BETWEEN a.st_time - INTERVAL '5 minutes'
                                                 AND a.st_time + INTERVAL '5 minutes'
                        ORDER BY ABS(EXTRACT(EPOCH FROM (e2.start_time - a.st_time)))
                        LIMIT 1
                    )
                """)}
                LEFT JOIN syobocal_titles t ON t.tid = a.tid
                WHERE a.cnt IS NOT NULL
                  AND a.st_time >= @lo AND a.st_time <= @hi
                  AND (t.first_ym IS NULL OR t.first_ym = 0 OR t.first_ym >= @coveredFromYm)
                  AND NOT EXISTS (
                      SELECT 1 FROM syobocal_airings a2
                      WHERE a2.tid = a.tid AND a2.cnt = a.cnt AND a2.st_time < a.st_time
                  )
                """;
            cmd.Parameters.AddWithValue("@lo", lo);
            cmd.Parameters.AddWithValue("@hi", hi);
            cmd.Parameters.AddWithValue("@coveredFromYm", coveredFromYm);
            using var r = cmd.ExecuteReader();
            var set = new HashSet<(string, DateTime)>();
            while (r.Read())
                if (!r.IsDBNull(0) && !r.IsDBNull(1))
                {
                    var t = r.GetDateTime(1);
                    set.Add((r.GetString(0), t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute))));
                }
            return set;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return null; }
    }

    /// <summary>
    /// 合成 events 行の event_id の下駄。実 EPG の event_id は 16bit（最大 65535）なので、
    /// これを足した値は実データと絶対に衝突しない。合成行だけを消したいときは
    /// DELETE FROM events WHERE event_id >= 1000000 でよい。
    /// </summary>
    public const int SyntheticEventIdBase = 1_000_000;

    /// <summary>
    /// しょぼカルの放送データから events 行を作る（EPG が無い期間の番組表用）。
    ///
    /// EDCB の EPG は 2026-06 からしか無い。それ以前を番組表で見られるようにするため、
    /// しょぼカルが持っている放送予定を events の形に変換して入れる。
    /// event_id は放送ID(PID)に <see cref="SyntheticEventIdBase"/> を足したもので、
    /// 実 EPG の行を書き換えることはない。
    ///
    /// 入るのはアニメのみ・syobocal_service_map に対応のあるチャンネルのみ。
    /// 番組名は「作品名 #話数 サブタイトル」、説明は作品解説（話ごとではない）。
    /// </summary>
    /// <returns>書き込んだ行数。失敗時は -1。</returns>
    public int BuildSyntheticEvents(DateTime lo, DateTime hi)
    {
        LastSyobocalError = null;
        if (!IsConfigured) return -1;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();

            int n;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 300;
                cmd.CommandText = $"""
                    -- しょぼカルの1チャンネル(chid)に対して、EDCB 側は複数サービスが
                    -- 対応しうる。そのまま結合すると1つの放送が全サービスに複製され、
                    -- 過去の番組表だけ同じ番組が何列も並ぶ。chid ごとに1つへ絞る。
                    --   ・同じ局名の枝番(ＢＳ日テレ 141/142/143 等)
                    --     → 実 EPG で番組名が入るのは本編だけ(142/143 は枠だけで名前が空)
                    --       なので、番組名が入っている行が最も多いものを本編とみなす。
                    --       sid の小ささでは選ばない(フジテレビは 1056 が本編だが 1440 も
                    --       実際に使われており、単純な最小値では取り違える)
                    --   ・4K 局(ＢＳ日テレ　４Ｋ 等)は前方一致で同じ chid に付いてしまう
                    --     → しょぼカルは 4K を区別して持たないので候補から外す。
                    --       onid=11 が 4K 専用ネットワーク(全8サービスが 4K、他の onid には無い)
                    WITH main_service AS (
                        SELECT DISTINCT ON (sm.chid)
                               sm.chid, s.onid, s.tsid, s.sid
                        FROM syobocal_service_map sm
                        JOIN services s ON s.service_name = sm.service_name
                        LEFT JOIN events e
                               ON e.onid = s.onid AND e.tsid = s.tsid AND e.sid = s.sid
                              AND e.event_id < {SyntheticEventIdBase}
                              AND e.event_name <> ''
                        WHERE s.service_type = 1 AND s.partial_reception = 0
                          AND s.onid <> 11
                        GROUP BY sm.chid, s.onid, s.tsid, s.sid
                        ORDER BY sm.chid, count(e.event_id) DESC, s.onid, s.sid
                    )
                    INSERT INTO events (
                        onid, tsid, sid, event_id, start_time, duration_sec,
                        event_name, short_text, ext_text,
                        component_stream_content, component_type, component_tag, component_text,
                        free_ca_flag, updated_at, year_week, reserve_status)
                    SELECT s.onid, s.tsid, s.sid, a.pid + {SyntheticEventIdBase}, a.st_time,
                           CASE WHEN a.ed_time IS NULL THEN NULL
                                ELSE GREATEST(0, EXTRACT(EPOCH FROM (a.ed_time - a.st_time))::int) END,
                           left(trim(coalesce(t.title, '') ||
                                CASE WHEN a.cnt IS NULL THEN '' ELSE ' #' || a.cnt END ||
                                CASE WHEN a.sub_title = '' THEN '' ELSE ' ' || a.sub_title END), 512),
                           a.sub_title,
                           coalesce(t.comment, ''),
                           NULL, NULL, NULL, '',
                           0, now(), 0, 0
                    FROM syobocal_airings a
                    JOIN main_service s ON s.chid = a.chid
                    LEFT JOIN syobocal_titles t ON t.tid = a.tid
                    WHERE a.st_time >= @lo AND a.st_time < @hi
                    ON CONFLICT (onid, tsid, sid, event_id) DO UPDATE SET
                        start_time   = EXCLUDED.start_time,
                        duration_sec = EXCLUDED.duration_sec,
                        event_name   = EXCLUDED.event_name,
                        short_text   = EXCLUDED.short_text,
                        ext_text     = EXCLUDED.ext_text,
                        updated_at   = EXCLUDED.updated_at
                    """;
                cmd.Parameters.AddWithValue("@lo", lo);
                cmd.Parameters.AddWithValue("@hi", hi);
                n = cmd.ExecuteNonQuery();
            }

            // ジャンルは全てアニメ（EDCB のジャンル大分類 7）
            using (var g = conn.CreateCommand())
            {
                g.CommandTimeout = 300;
                g.CommandText = $"""
                    INSERT INTO event_genres (onid, tsid, sid, event_id, seq,
                                              nibble_l1, nibble_l2, user_nibble_1, user_nibble_2)
                    SELECT e.onid, e.tsid, e.sid, e.event_id, 0, 7, 15, 15, 15
                    FROM events e
                    WHERE e.event_id >= {SyntheticEventIdBase}
                      AND e.start_time >= @lo AND e.start_time < @hi
                    ON CONFLICT (onid, tsid, sid, event_id, seq) DO NOTHING
                    """;
                g.Parameters.AddWithValue("@lo", lo);
                g.Parameters.AddWithValue("@hi", hi);
                g.ExecuteNonQuery();
            }
            return n;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return -1; }
    }

    /// <summary>
    /// events が無い期間の最速判定。しょぼカルと手持ちファイルだけで判定する。
    ///
    /// events の蓄積は 2026-06 からで、それ以前の録画は EPG 側に対応する行が無いため
    /// <see cref="GetFastestKeysViaJoin"/> では一件も拾えない。しょぼカルは
    /// それより前から放送予定を持っているので、EPG の代わりにファイル自身を突き合わせ先にする。
    ///
    /// events を使う版との違いは「放送が実在した証拠」を何に求めるかだけで、
    /// ここでは手元に録画ファイルがあること自体を証拠とする。
    /// 話数が無い放送・カバー範囲より前に始まった作品を除く条件は同じ。
    /// </summary>
    public HashSet<(string ServiceName, DateTime StartTime)>? GetFastestKeysFromFilesOnly(
        DateTime lo, DateTime hi, int coveredFromYm,
        IReadOnlyCollection<(string Station, DateTime Time)> fileKeys)
    {
        LastSyobocalError = null;
        if (!IsConfigured || fileKeys.Count == 0) return null;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            CreateFileKeyTempTable(conn, fileKeys);

            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 60;
            // 1つの放送に複数のファイルがぶら下がらないよう、±5分で最も近い1件に限定する。
            // events 版と同じ理由（数分差の別番組を道連れにする）。
            cmd.CommandText = """
                SELECT DISTINCT fk.service_name, fk.start_time
                FROM syobocal_airings a
                JOIN syobocal_service_map sm ON sm.chid = a.chid
                JOIN tmp_file_keys fk ON fk.service_name = sm.service_name
                    AND fk.start_time = (
                        SELECT fk2.start_time FROM tmp_file_keys fk2
                        WHERE fk2.service_name = sm.service_name
                          AND fk2.start_time BETWEEN a.st_time - INTERVAL '5 minutes'
                                                 AND a.st_time + INTERVAL '5 minutes'
                        ORDER BY ABS(EXTRACT(EPOCH FROM (fk2.start_time - a.st_time)))
                        LIMIT 1
                    )
                LEFT JOIN syobocal_titles t ON t.tid = a.tid
                WHERE a.cnt IS NOT NULL
                  AND a.st_time >= @lo AND a.st_time <= @hi
                  AND (t.first_ym IS NULL OR t.first_ym = 0 OR t.first_ym >= @coveredFromYm)
                  AND NOT EXISTS (
                      SELECT 1 FROM syobocal_airings a2
                      WHERE a2.tid = a.tid AND a2.cnt = a.cnt AND a2.st_time < a.st_time
                  )
                """;
            cmd.Parameters.AddWithValue("@lo", lo);
            cmd.Parameters.AddWithValue("@hi", hi);
            cmd.Parameters.AddWithValue("@coveredFromYm", coveredFromYm);
            using var r = cmd.ExecuteReader();
            var set = new HashSet<(string, DateTime)>();
            while (r.Read())
                if (!r.IsDBNull(0) && !r.IsDBNull(1))
                {
                    var t = r.GetDateTime(1);
                    set.Add((r.GetString(0), t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute))));
                }
            return set;
        }
        catch (Exception ex) { LastSyobocalError = ex.Message; return null; }
    }

    /// <summary>
    /// 手持ちの録画ファイルの (局名, 開始時刻[分単位]) を一時テーブルに載せる。
    /// service_name は services.service_name と同じ型・照合順序（varchar(256) utf8mb4_0900_ai_ci）に
    /// 揃えないと、結合時に文字コード変換が入って索引が効かなくなる。
    /// </summary>
    private static void CreateFileKeyTempTable(
        NpgsqlConnection conn, IReadOnlyCollection<(string Station, DateTime Time)> fileKeys)
    {
        using (var drop = conn.CreateCommand())
        {
            // 接続プール経由で同じ接続が再利用されると前回の一時テーブルが残るため必ず落とす
            drop.CommandText = "DROP TABLE IF EXISTS tmp_file_keys";
            drop.ExecuteNonQuery();
        }
        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TEMP TABLE tmp_file_keys (
                    service_name VARCHAR(256) NOT NULL,
                    start_time   TIMESTAMP NOT NULL,
                    PRIMARY KEY (service_name, start_time)
                )
                """;
            create.ExecuteNonQuery();
        }
        foreach (var chunk in fileKeys.Chunk(500))
        {
            using var ins = conn.CreateCommand();
            var values = new List<string>();
            int i = 0;
            foreach (var (station, time) in chunk)
            {
                values.Add($"(@n{i},@s{i})");
                ins.Parameters.AddWithValue($"@n{i}", station);
                // 秒を切り捨てて分単位に揃える（PostgreSQL は timestamp 列に文字列を渡せない）
                ins.Parameters.AddWithValue($"@s{i}", time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMinute)));
                i++;
            }
            if (values.Count == 0) continue;
            // 同一局・同一分のファイルが複数あっても落とさない
            ins.CommandText =
                "INSERT INTO tmp_file_keys (service_name, start_time) VALUES " +
                string.Join(",", values);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>services テーブルの全サービス名（しょぼカルチャンネルの逆引き用）。</summary>
    public List<string> GetServiceNames()
    {
        if (!IsConfigured) return [];
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT service_name FROM services";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read())
                if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            return list;
        }
        catch { return []; }
    }

    /// <summary>events テーブルの最古の開始時刻（蓄積開始点）。取得不能なら null。</summary>
    public DateTime? GetEventsMinStartTime()
    {
        if (!IsConfigured) return null;
        try
        {
            using var conn = new NpgsqlConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MIN(start_time) FROM events";
            var v = cmd.ExecuteScalar();
            return v is DateTime dt ? dt
                 : v is string s && DateTime.TryParse(s, out var p) ? p : null;
        }
        catch { return null; }
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

    private static List<EpgEvent> ReadEvents(NpgsqlDataReader r)
    {
        var list = new List<EpgEvent>();
        while (r.Read())
            list.Add(new EpgEvent
            {
                ONID        = (ushort)r.GetInt32(0),
                TSID        = (ushort)r.GetInt32(1),
                SID         = (ushort)r.GetInt32(2),
                EventID     = r.GetInt32(3),
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
