using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace EDCBViewer.Services;

/// <summary>
/// しょぼいカレンダー (https://cal.syoboi.jp/) 連携。
/// アニメの放送レコード（作品ID・話数・チャンネル・開始時刻）を取得し、
/// MySQL の syobocal_* 専用テーブルに保存する（events テーブルは一切変更しない）。
///
/// - 最速判定は保存後、EpgDbReader.GetFastestKeysViaJoin が syobocal_* を events と
///   JOIN して計算する（タイトル文字列は使わず (チャンネル, 開始時刻) → (TID, 話数) の
///   照合で行うため、局によるタイトル表記の違いに影響されない）
/// - 最速比較は TV 放送チャンネルのみ（ABEMA 等の配信・ラジオは ChGID で除外）
/// - API 負荷配慮: 2か月チャンクで取得、リクエスト間 2秒、429 はバックオフ再試行、
///   直近2か月のみ最短1時間間隔で再取得
/// - 同期の進捗（カバー済み月範囲）は syobocal_meta テーブルに持つため、
///   どのマシンが最初に同期しても以後は他マシンが読むだけで済む
/// </summary>
public sealed class SyobocalService
{
    private const string BaseUrl = "https://cal.syoboi.jp/db.php";
    // 700ms 間隔では十数リクエストで 429 (Too Many Requests) になることを実測済み。
    // 2秒間隔 + 429 時バックオフで運用する
    private const int RequestDelayMs = 2000;
    private static readonly TimeSpan RecentRefreshMinInterval = TimeSpan.FromHours(1);

    // ChGID: 7=ネット配信サイト, 10/12/15/16/24=ラジオ, 17=なし, 23=Web配信(ABEMA等)。
    // これら以外（地上波・BS・CS・地方局・4K/8K）を TV 放送として最速比較の対象にする
    internal static readonly HashSet<int> NonTvChGids = [7, 10, 12, 15, 16, 17, 23, 24];

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("EDCBViewer/1.0");
        return c;
    }

    // SubTitles の1行: *01*サブタイトル
    private static readonly System.Text.RegularExpressions.Regex SubTitleLine =
        new(@"^\*(\d+)\*(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WikiLink =
        new(@"\[\[([^\]]+)\]\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BlankRuns =
        new(@"\n{3,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal sealed record ChRow(int ChID, string Name, string EpgName, int Gid);
    internal sealed record ProgRow(int PID, int TID, int? Count, int ChID, DateTime StTime,
                                   DateTime? EdTime, string SubTitle);

    // ─── 同期 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ファイル群（放送局, 開始時刻）をカバーするよう syobocal_* テーブルを整備する。
    /// 戻り値はDBが更新された（読み直す価値がある）とき true。
    /// DB のカバー範囲が既に十分ならAPI通信せずに済む。オフライン時は例外を握りつぶす。
    /// </summary>
    public async Task<bool> SyncToDbAsync(
        EpgDbReader reader,
        IReadOnlyCollection<(string Station, DateTime Time)> fileKeys,
        Action<string>? progress = null)
    {
        if (fileKeys.Count == 0) return false;
        var stations = fileKeys.Select(k => k.Station).Distinct().ToList();
        // 取得範囲は手持ちファイルの期間に合わせる。
        // 以前は events の蓄積開始(2026-06)でクランプしていたが、それ以前の録画も
        // しょぼカルとファイルだけで最速判定できるようにしたため、遡って取得する
        // （EpgDbReader.GetFastestKeysFromFilesOnly）。
        var minTime = fileKeys.Min(k => k.Time);
        var maxTime = fileKeys.Max(k => k.Time);
        if (maxTime > DateTime.Now) maxTime = DateTime.Now;
        if (maxTime < minTime) maxTime = minTime;

        var changed = false;
        try
        {
            if (!reader.EnsureSyobocalTables())
            {
                Log($"EnsureSyobocalTables failed: {reader.LastSyobocalError}");
                return false;
            }

            progress?.Invoke("しょぼカル: チャンネル一覧取得中...");
            var channels = ParseChannels(await GetAsync("Command=ChLookup"));
            Log($"ChLookup: {channels.Count} channels");

            var (coveredFrom, coveredTo, lastRecentRefresh) = reader.GetSyobocalMeta();

            var wantFrom = Ym(minTime.AddMonths(-1));
            var wantTo   = Ym(maxTime);
            var chunks = BuildFetchChunks(coveredFrom, coveredTo, wantFrom, wantTo);

            // 直近2か月は改編・時刻変更があるので定期的に取り直す
            var recentChunks = new List<(int From, int To)>();
            if (chunks.Count == 0 && coveredFrom != 0 &&
                (lastRecentRefresh == null || DateTime.Now - lastRecentRefresh > RecentRefreshMinInterval))
            {
                var from = Math.Max(Ym(DateTime.Now.AddMonths(-1)), coveredFrom);
                var to   = Math.Min(Ym(DateTime.Now), coveredTo);
                if (from <= to) recentChunks.Add((from, to));
            }

            if (chunks.Count > 0 || recentChunks.Count > 0)
                Log($"sync start: files={fileKeys.Count} want={wantFrom}-{wantTo} " +
                    $"covered={coveredFrom}-{coveredTo} chunks={chunks.Count} recent={recentChunks.Count}");

            var allTids = new HashSet<int>();
            var all = chunks.Concat(recentChunks).ToList();
            var anyFailed = false;
            for (int i = 0; i < all.Count; i++)
            {
                progress?.Invoke($"しょぼカル同期中 ({i + 1}/{all.Count})...");
                var (fromYm, toYm) = all[i];
                try
                {
                    var lo = new DateTime(fromYm / 100, fromYm % 100, 1);
                    var hi = new DateTime(toYm / 100, toYm % 100, 1).AddMonths(1);
                    var tids = await FetchRangeToDbAsync(reader, channels, lo, hi);
                    allTids.UnionWith(tids);
                    changed = true;
                    // 失敗で全損しないよう、カバー範囲をチャンクごとに DB へ保存する
                    // （BuildFetchChunks の並びは連続性を保つ: 下方向は降順・上方向は昇順）
                    if (i < chunks.Count)
                    {
                        coveredFrom = coveredFrom == 0 ? fromYm : Math.Min(coveredFrom, fromYm);
                        coveredTo   = Math.Max(coveredTo, toYm);
                        reader.SetSyobocalMeta(coveredFrom, coveredTo, lastRecentRefresh);
                    }
                }
                catch (Exception ex)
                {
                    // バックオフしても失敗が続く場合は残りを諦めて次回に再開する
                    // （途中の月を飛ばすとカバー範囲の連続性が壊れるため中断が正しい）
                    anyFailed = true;
                    Log($"chunk {fromYm}-{toYm} FAILED, aborting rest: {ex}");
                    break;
                }
            }
            if (all.Count > 0)
                Log($"sync chunks done: covered={coveredFrom}-{coveredTo} failed={anyFailed}");
            if (recentChunks.Count > 0 && !anyFailed)
            {
                lastRecentRefresh = DateTime.Now;
                reader.SetSyobocalMeta(coveredFrom, coveredTo, lastRecentRefresh);
            }

            // サービス対応表（今回対象の局のみ）を更新
            var map = BuildStationMap(stations, channels);
            reader.ReplaceServiceMap(map);

            // 今回取得範囲に登場した作品の放送開始年月を補充（カバー範囲外ガード用）
            var missing = reader.GetMissingTitleIds(allTids.ToList());
            if (missing.Count > 0)
            {
                progress?.Invoke("しょぼカル: 作品情報取得中...");
                Log($"TitleLookup: {missing.Count} tids");
                var result = new Dictionary<int, TitleRow>();
                foreach (var chunk in missing.Chunk(50))
                {
                    foreach (var kv in ParseTitleFirstYm(
                        await GetAsync("Command=TitleLookup&TID=" + string.Join(",", chunk))))
                        result[kv.Key] = kv.Value;
                    // 応答に含まれなかった分も再問合せしないよう記録
                    foreach (var tid in chunk)
                        result.TryAdd(tid, new TitleRow(0, "", "", []));
                }
                reader.UpsertTitleFirstYm(result);
                changed = true;
            }

            // 取得するものが無くても必ず表示を終わらせる。
            // これが無いと「チャンネル一覧取得中...」が出たまま残り、
            // 延々と取得しているように見える（直近分の再取得は間隔を空けるので、
            // しばらくは毎回「何も取得しない」経路を通る）。
            progress?.Invoke(anyFailed ? "しょぼカル同期に一部失敗（syobocal.log 参照）" : "");
        }
        catch (Exception ex)
        {
            Log($"sync FAILED: {ex}");
            progress?.Invoke("しょぼカル同期失敗（syobocal.log 参照）");
        }
        return changed;
    }

    // 取得対象を最大2か月のチャンクにして「途中で失敗してもカバー範囲の連続性が保てる順」で返す:
    // 初回は昇順、既存範囲の下方向への拡張は降順（coveredFrom 側から）、上方向への拡張は昇順
    internal static List<(int From, int To)> BuildFetchChunks(
        int coveredFrom, int coveredTo, int wantFrom, int wantTo)
    {
        var chunks = new List<(int, int)>();
        void AddAscending(int from, int to)
        {
            for (var ym = from; ym <= to;)
            {
                var next = NextYm(ym);
                var end = next <= to ? next : ym;
                chunks.Add((ym, end));
                ym = NextYm(end);
            }
        }
        if (coveredFrom == 0)
        {
            AddAscending(wantFrom, wantTo);
            return chunks;
        }
        for (var hi = PrevYm(coveredFrom); hi >= wantFrom;)
        {
            var lo = PrevYm(hi);
            if (lo < wantFrom) lo = hi;
            chunks.Add((lo, hi));
            hi = PrevYm(lo);
        }
        if (coveredTo < wantTo) AddAscending(NextYm(coveredTo), wantTo);
        return chunks;
    }

    private static int Ym(DateTime t) => t.Year * 100 + t.Month;
    private static int NextYm(int ym) => ym % 100 == 12 ? ym + 89 : ym + 1;
    private static int PrevYm(int ym) => ym % 100 == 1  ? ym - 89 : ym - 1;

    // Range 指定で取得して syobocal_airings に反映。5000件上限に達したら半分に割って再帰。
    // 切り捨て判定は Deleted 行も含む生の件数で行うこと
    // （有効行だけで判定すると 5000 件ちょうどの応答に Deleted が混ざったとき
    //  4999 に届かず、末尾が欠けたまま保存してしまう。7/17 以降が欠落した実績あり）
    private async Task<HashSet<int>> FetchRangeToDbAsync(
        EpgDbReader reader, List<ChRow> channels, DateTime lo, DateTime hi)
    {
        var xml = await GetAsync($"Command=ProgLookup&Range={lo:yyyyMMdd_HHmmss}-{hi:yyyyMMdd_HHmmss}");
        var (rows, deletedPids) = ParseProgs(xml);
        var rawCount = rows.Count + deletedPids.Count;
        if (rawCount >= 4900 && (hi - lo) > TimeSpan.FromDays(1))
        {
            var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
            var t1 = await FetchRangeToDbAsync(reader, channels, lo, mid);
            var t2 = await FetchRangeToDbAsync(reader, channels, mid, hi);
            t1.UnionWith(t2);
            return t1;
        }
        if (rawCount >= 4900)
            Log($"WARN: possible truncation at {lo:yyyyMMdd}-{hi:yyyyMMdd} raw={rawCount}");

        var tvChids = channels.Where(c => !NonTvChGids.Contains(c.Gid)).Select(c => c.ChID).ToHashSet();
        var keep = rows.Where(r => tvChids.Contains(r.ChID)).ToList();
        if (!reader.ReplaceAiringsInRange(lo, hi,
                keep.Select(r => (r.PID, r.TID, r.Count, r.ChID, r.StTime, r.EdTime, r.SubTitle)).ToList()))
            throw new InvalidOperationException($"ReplaceAiringsInRange failed: {reader.LastSyobocalError}");
        return keep.Select(r => r.TID).ToHashSet();
    }

    private async Task<string> GetAsync(string query)
    {
        for (int attempt = 0; ; attempt++)
        {
            await Task.Delay(RequestDelayMs);
            try
            {
                return await Http.GetStringAsync($"{BaseUrl}?{query}");
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 3)
            {
                // レート制限。待ってから同じリクエストを再試行（15秒 → 30秒 → 60秒）
                var wait = TimeSpan.FromSeconds(15 << attempt);
                Log($"429 backoff {wait.TotalSeconds}s: {query}");
                await Task.Delay(wait);
            }
        }
    }

    // ─── チャンネル対応付け ─────────────────────────────────────────────────

    // EDCB サービス名（正規化後）→ しょぼカル ChName の手動対応（自動照合で拾えないもの）
    private static readonly Dictionary<string, string> ChannelAliases = new()
    {
        ["NHKBSP4K"] = "NHK BSプレミアム4K",
        ["NHKBS"]    = "NHK-BS1",
        ["テレ東"]    = "テレビ東京",
        ["テレ東1"]   = "テレビ東京",
        ["日テレ"]    = "日本テレビ",
        ["日テレ1"]   = "日本テレビ",
    };

    /// <summary>
    /// EDCB のサービス名としょぼカルチャンネルの対応付け。
    /// NFKC 正規化 + 空白・中点・ハイフン除去 + 大文字化した名前で
    /// 完全一致 → 前方一致（EDCB 側の末尾サブチャンネル番号等を吸収）→ 包含 の順に照合。
    /// 候補は複数持てる（同時刻の放送はないため照合時に自然に絞られる）。
    /// </summary>
    internal static Dictionary<string, List<int>> BuildStationMap(
        IReadOnlyCollection<string> stations, List<ChRow> channels)
    {
        var chans = channels
            .Where(c => !NonTvChGids.Contains(c.Gid))
            .Select(c => (c.ChID, Name: NormalizeName(c.Name), Epg: NormalizeName(c.EpgName)))
            .ToList();

        var map = new Dictionary<string, List<int>>();
        foreach (var station in stations.Distinct())
        {
            var n = NormalizeName(station);
            if (n.Length == 0) { map[station] = []; continue; }
            if (ChannelAliases.TryGetValue(n, out var alias))
            {
                var nAlias = NormalizeName(alias);
                map[station] = chans.Where(c => c.Name == nAlias).Select(c => c.ChID).ToList();
                continue;
            }

            var exact = chans.Where(c => c.Name == n || c.Epg == n).Select(c => c.ChID).ToList();
            if (exact.Count > 0) { map[station] = exact; continue; }

            var prefix = chans.Where(c =>
                    (c.Name.Length >= 2 && n.StartsWith(c.Name)) ||
                    (c.Epg.Length  >= 2 && n.StartsWith(c.Epg))  ||
                    (n.Length >= 2 && (c.Name.StartsWith(n) || c.Epg.StartsWith(n))))
                .Select(c => c.ChID).Distinct().ToList();
            if (prefix.Count > 0) { map[station] = prefix; continue; }

            map[station] = chans.Where(c =>
                    (c.Name.Length >= 5 && n.Contains(c.Name)) ||
                    (c.Epg.Length  >= 5 && n.Contains(c.Epg)))
                .Select(c => c.ChID).Distinct().ToList();
        }
        return map;
    }

    internal static string NormalizeName(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
            if (!char.IsWhiteSpace(ch) && ch != '・' && ch != '-' && ch != '−' && ch != '－')
                sb.Append(ch);
        return sb.ToString();
    }

    // ─── XML パース ─────────────────────────────────────────────────────────

    // ProgComment 等は人力入力のため、XML 1.0 で不正な制御文字が混ざることに備えて除去する
    internal static string SanitizeXml(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20)
                sb.Append(ch);
        return sb.ToString();
    }

    internal static List<ChRow> ParseChannels(string xml) =>
        XDocument.Parse(SanitizeXml(xml)).Descendants("ChItem")
            .Select(x => new ChRow(
                (int?)x.Element("ChID") ?? 0,
                (string?)x.Element("ChName") ?? "",
                (string?)x.Element("ChiEPGName") ?? "",
                int.TryParse((string?)x.Element("ChGID"), out var g) ? g : -1))
            .Where(c => c.ChID > 0)
            .ToList();

    internal static (List<ProgRow> Rows, List<int> DeletedPids) ParseProgs(string xml)
    {
        var rows = new List<ProgRow>();
        var deleted = new List<int>();
        foreach (var x in XDocument.Parse(SanitizeXml(xml)).Descendants("ProgItem"))
        {
            var pid = (int?)x.Element("PID") ?? 0;
            if (pid == 0) continue;
            if (((string?)x.Element("Deleted") ?? "0") != "0") { deleted.Add(pid); continue; }
            if (!DateTime.TryParse((string?)x.Element("StTime"), out var st)) continue;
            int? count = int.TryParse((string?)x.Element("Count"), out var c) ? c : null;
            // EdTime は番組表の枠の高さに使う。無い放送もあるので null 許容
            DateTime? ed = DateTime.TryParse((string?)x.Element("EdTime"), out var e) ? e : null;
            rows.Add(new ProgRow(pid, (int?)x.Element("TID") ?? 0, count,
                                 (int?)x.Element("ChID") ?? 0, st, ed,
                                 ((string?)x.Element("SubTitle") ?? "").Trim()));
        }
        return (rows, deleted);
    }

    /// <summary>作品マスタ1件分。話数タイトルは TitleLookup がまとめて返す。</summary>
    internal sealed record TitleRow(int FirstYm, string Title, string Comment,
                                    Dictionary<int, string> SubTitles);

    internal static Dictionary<int, TitleRow> ParseTitleFirstYm(string xml)
    {
        var result = new Dictionary<int, TitleRow>();
        foreach (var x in XDocument.Parse(SanitizeXml(xml)).Descendants("TitleItem"))
        {
            var tid = (int?)x.Element("TID") ?? 0;
            if (tid == 0) continue;
            var y = int.TryParse((string?)x.Element("FirstYear"),  out var yy) ? yy : 0;
            var m = int.TryParse((string?)x.Element("FirstMonth"), out var mm) ? mm : 0;
            result[tid] = new TitleRow(
                y > 0 && m > 0 ? y * 100 + m : 0,
                ((string?)x.Element("Title") ?? "").Trim(),
                CleanWiki((string?)x.Element("Comment") ?? ""),
                ParseSubTitles((string?)x.Element("SubTitles") ?? ""));
        }
        return result;
    }

    /// <summary>
    /// SubTitles は 1行1話で「*01*サブタイトル」の形式。話数 → タイトル にほぐす。
    /// 放送レコードを1件ずつ見なくても話数マスタが作れる。
    /// </summary>
    internal static Dictionary<int, string> ParseSubTitles(string s)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(s)) return result;
        foreach (var raw in s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var m = SubTitleLine.Match(raw.Trim());
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var cnt)) continue;
            result[cnt] = m.Groups[2].Value.Trim();   // 同じ話数が二度出たら後勝ち
        }
        return result;
    }

    /// <summary>
    /// しょぼカルの Comment は Wiki 記法で書かれている。番組表の説明欄に出すので、
    /// 記号を落として素の文章にする（スタッフ・キャストの行はそのまま残す）。
    /// </summary>
    internal static string CleanWiki(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder();
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { sb.Append('\n'); continue; }
            // 見出し (*, **, ***) は行頭の * を落とす
            line = line.TrimStart('*').Trim();
            // [[リンク]] / [[表示|リンク先]] は表示側だけ残す
            line = WikiLink.Replace(line, mm =>
            {
                var inner = mm.Groups[1].Value;
                var bar = inner.IndexOf('|');
                return bar >= 0 ? inner[..bar] : inner;
            });
            line = line.Replace("''", "");           // 強調
            sb.Append(line).Append('\n');
        }
        // 空行の連続を1つにまとめる
        return BlankRuns.Replace(sb.ToString(), "\n\n").Trim();
    }

    // ─── ログ ───────────────────────────────────────────────────────────────

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EDCBViewer", "syobocal.log");

    internal static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
        }
        catch { }
    }
}
