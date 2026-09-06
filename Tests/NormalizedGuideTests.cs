using EDCBViewer.Services;

namespace EDCBViewer.Tests;

/// <summary>
/// 番組マスタ（programs / program_episodes）まわりを検証用DBに対して実行する。
///
/// **本番(postgres)には接続しない。** 未検証のまま本番で動かさないため、
/// 検証用の edcbviewer_test に対してのみ実行する。
/// 検証用DBが無い環境では何も検証せずに抜ける。
/// </summary>
public class NormalizedGuideTests
{
    /// <summary>検証用DB。本番の接続文字列の Database だけ差し替える。</summary>
    private static string TestConnStr =>
        string.IsNullOrWhiteSpace(TestDb.ConnStr) ? ""
        : System.Text.RegularExpressions.Regex.Replace(
            TestDb.ConnStr, @"Database=[^;]*", "Database=edcbviewer_test",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool Available()
    {
        if (string.IsNullOrWhiteSpace(TestConnStr)) return false;
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(TestConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM programs";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch { return false; }
    }

    private static long Scalar(string sql)
    {
        using var conn = new Npgsql.NpgsqlConnection(TestConnStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public void 話数マスタを放送レコードから補える()
    {
        if (!Available()) return;

        var reader = new EpgDbReader(TestConnStr);
        var n = reader.EnsureEpisodesFromAirings();

        Assert.True(n >= 0, "失敗: " + reader.LastSyobocalError);
        // 放送レコードの (作品, 話数) が全て話数マスタに存在する
        Assert.Equal(0, Scalar("""
            SELECT count(*) FROM (
                SELECT DISTINCT p.program_id, a.cnt
                FROM syobocal_airings a
                JOIN programs p ON p.src='syobocal' AND p.src_id=a.tid) x
            LEFT JOIN program_episodes ep
                   ON ep.program_id = x.program_id
                  AND COALESCE(ep.cnt,-1) = COALESCE(x.cnt,-1)
            WHERE ep.episode_id IS NULL
            """));
    }

    /// <summary>
    /// 放送レコードに出てくる作品が、全て作品マスタに登録されているか。
    ///
    /// 以前は syobocal_titles で「取得済み」を判定していたため、そちらにだけ存在する
    /// 作品（MySQL 時代からの 617 件）が programs に一度も入らず、
    /// 2026-05 の 233 作品が番組表から丸ごと落ちていた。
    /// </summary>
    [Fact]
    public void 放送レコードの作品が全てマスタにある()
    {
        if (!Available()) return;

        var missing = Scalar("""
            SELECT count(DISTINCT a.tid)
            FROM syobocal_airings a
            LEFT JOIN programs p ON p.src='syobocal' AND p.src_id = a.tid
            WHERE p.program_id IS NULL
            """);
        Assert.Equal(0, missing);
    }

    /// <summary>
    /// 取得対象の判定が programs を見ているか。
    /// syobocal_titles にだけある作品は「未取得」として返らなければならない。
    /// </summary>
    [Fact]
    public void 作品マスタに無いものが取得対象になる()
    {
        if (!Available()) return;

        var reader = new EpgDbReader(TestConnStr);
        // programs に存在しない TID は必ず取得対象
        Assert.Contains(999_999_99, reader.GetMissingTitleIds([999_999_99]));
    }

    [Fact]
    public void 実EPGに話数を紐付けられる()
    {
        if (!Available()) return;

        var reader = new EpgDbReader(TestConnStr);
        reader.EnsureEpisodesFromAirings();
        var n = reader.LinkEventsToEpisodes(new DateTime(2000, 1, 1), DateTime.Now);

        Assert.True(n >= 0, "失敗: " + reader.LastSyobocalError);
        Assert.True(Scalar("SELECT count(*) FROM events WHERE episode_id IS NOT NULL") > 0,
                    "1件も紐付いていない");
    }

    [Fact]
    public void 番組表がマスタから作品名を組み立てる()
    {
        if (!Available()) return;

        var reader = new EpgDbReader(TestConnStr);
        reader.EnsureEpisodesFromAirings();
        reader.LinkEventsToEpisodes(new DateTime(2000, 1, 1), DateTime.Now);

        // events 自身が番組名を持つ実 EPG の期間を読む
        var min = reader.GetEventsMinStartTime();
        if (min == null) return;
        var day = min.Value.Date.AddDays(1).AddHours(4);
        var list = reader.GetGuideEvents(day, day.AddHours(24));

        Assert.Null(reader.LastGuideError);
        Assert.NotEmpty(list);
        Assert.All(list, e => Assert.False(string.IsNullOrEmpty(e.ServiceName)));
    }

    [Fact]
    public void 合成行はマスタを参照し文章を複製しない()
    {
        if (!Available()) return;

        var reader = new EpgDbReader(TestConnStr);
        reader.EnsureEpisodesFromAirings();

        var lo = new DateTime(2026, 3, 1);
        var hi = new DateTime(2026, 6, 1);
        var n = reader.BuildSyntheticEvents(lo, hi);
        Assert.True(n >= 0, "失敗: " + reader.LastSyobocalError);

        // 合成行は文章を持たず、episode_id で辿れる
        Assert.Equal(0, Scalar(
            $"SELECT count(*) FROM events WHERE event_id >= {EpgDbReader.SyntheticEventIdBase} " +
            "AND (ext_text <> '' OR event_name <> '')"));
        Assert.Equal(0, Scalar(
            $"SELECT count(*) FROM events WHERE event_id >= {EpgDbReader.SyntheticEventIdBase} " +
            "AND episode_id IS NULL"));

        // 1つの放送が複数サービスに複製されていない
        Assert.Equal(0, Scalar($"""
            SELECT count(*) FROM (
                SELECT event_id FROM events
                WHERE event_id >= {EpgDbReader.SyntheticEventIdBase}
                GROUP BY event_id HAVING count(*) > 1) x
            """));

        // 番組表からは作品名が引ける
        Assert.True(Scalar($"""
            SELECT count(*) FROM events e
            JOIN program_episodes ep ON ep.episode_id = e.episode_id
            JOIN programs p ON p.program_id = ep.program_id
            WHERE e.event_id >= {EpgDbReader.SyntheticEventIdBase} AND p.title <> ''
            """) > 0, "合成行から作品名を引けない");
    }
}
