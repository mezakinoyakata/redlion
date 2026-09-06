using EDCBViewer.Services;

namespace EDCBViewer.Tests;

/// <summary>
/// MySQL から PostgreSQL(Supabase)へ移行した SQL が、実際に実行できるかを確認する。
///
/// ビルドが通っても SQL 方言の誤り(ON DUPLICATE KEY / REGEXP / DATE_SUB など)は
/// 実行時にしか判明しないため、実 DB に対して流して確かめる。
///
/// DB に接続できない環境では、何も検証せずに成功扱いで抜ける。
/// </summary>
public class PostgresQueryTests
{
    // 接続先は settings.json から読む（TestDb 参照。ソースには書かない）
    private static string ConnStr => TestDb.ConnStr;

    private static bool DbAvailable() => TestDb.Available();

    [Fact]
    public void 検索クエリが実行できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        // 正規表現(~)と、フォールバックの LIKE の両経路を通す
        var hits = reader.SearchEvents("ニュース", 10);

        Assert.Null(reader.LastSearchError);   // 方言エラーはここに出る
        Assert.NotNull(hits);
    }

    [Fact]
    public void 番組情報の取得が実行できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        // 実在しない組み合わせでも、SQL が通れば null が返るだけ
        Assert.Null(reader.GetEventInfoText(0, 0, 0, 0));
    }

    /// <summary>
    /// 番組表。smallint 列(free_ca_flag / nibble_l1)を GetInt32 で読んでいるので、
    /// Npgsql の型チェックに引っかからないかをここで確かめる。
    /// </summary>
    [Fact]
    public void 番組表が取得できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        var day  = DateTime.Today.AddHours(4);      // 実画面と同じ 04:00 起点の24時間
        var list = reader.GetGuideEvents(day, day.AddHours(24));

        Assert.Null(reader.LastGuideError);   // 型の不一致などはここに出る
        Assert.NotEmpty(list);                // 取り込み済みなら今日の番組が空になることはない
        Assert.Contains(list, e => e.EventName.Length > 0 && e.ServiceName.Length > 0);
    }

    /// <summary>
    /// events が無い期間（2026-06 より前）の最速判定。
    /// events と JOIN しない別クエリなので、実 DB で通ることを確かめる。
    /// </summary>
    [Fact]
    public void events以前の最速判定クエリが実行できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        var (coveredFrom, _, _) = reader.GetSyobocalMeta();
        var min = reader.GetEventsMinStartTime();
        if (min == null || coveredFrom == 0) return;   // しょぼカル未整備

        // events 蓄積開始より前のファイルを想定する
        var fileKeys = new[] { ("ＮＨＫ総合１・東京", min.Value.AddDays(-30)) };
        var keys = reader.GetFastestKeysFromFilesOnly(
            min.Value.AddDays(-60), min.Value, coveredFrom, fileKeys);

        Assert.True(keys != null, "クエリ失敗: " + reader.LastSyobocalError);
    }

    /// <summary>
    /// しょぼカルからの合成 events 行を作るクエリ。
    /// 実データを増やさないよう、しょぼカルのデータが無い期間を指定して構文と型だけ確かめる。
    /// </summary>
    [Fact]
    public void 合成events行の生成クエリが実行できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        Assert.True(reader.EnsureSyobocalTables(), reader.LastSyobocalError);   // 列追加を先に

        var n = reader.BuildSyntheticEvents(new DateTime(1990, 1, 1), new DateTime(1990, 2, 1));

        Assert.True(n >= 0, "クエリ失敗: " + reader.LastSyobocalError);
        Assert.Equal(0, n);   // この期間にしょぼカルのデータは無い
    }

    /// <summary>
    /// EPG が無い期間（しょぼカル由来の合成行）を番組表として読めるか。
    /// DB には行があるのに画面に出ない、という切り分け用。
    /// </summary>
    [Fact]
    public void EPGが無い期間の番組表を取得できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        var day  = new DateTime(2020, 1, 1, 4, 0, 0);   // 実画面と同じ 04:00 起点
        var list = reader.GetGuideEvents(day, day.AddHours(24));

        Assert.Null(reader.LastGuideError);
        // その期間を未取得の環境では検証しない
        if (reader.GetEventsMinStartTime() is not { } min || min > day) return;

        Assert.NotEmpty(list);
        // 番組名がマスタから組み立てられている（event_name は空のはず）
        Assert.Contains(list, e => e.EventName.Length > 0);
        Assert.All(list, e => Assert.False(string.IsNullOrEmpty(e.ServiceName)));
    }

    [Fact]
    public void サービス名一覧が取得できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        Assert.NotEmpty(reader.GetServiceNames());
    }

    [Fact]
    public void eventsの最古開始時刻が取得できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        Assert.NotNull(reader.GetEventsMinStartTime());
    }

    [Fact]
    public void しょぼカルのテーブル作成が実行できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        Assert.True(reader.EnsureSyobocalTables(), reader.LastSyobocalError);
    }

    [Fact]
    public void 最速判定クエリが実行できる()
    {
        if (!DbAvailable()) return;

        var reader = new EpgDbReader(ConnStr);
        var (coveredFrom, _, _) = reader.GetSyobocalMeta();
        var min = reader.GetEventsMinStartTime();
        if (min == null || coveredFrom == 0) return;   // しょぼカル未整備

        // 一時テーブル作成 → INSERT → JOIN まで一通り通す
        var fileKeys = new[] { ("ＮＨＫ総合１・東京", min.Value.AddDays(1)) };
        var keys = reader.GetFastestKeysViaJoin(min.Value, DateTime.Now, coveredFrom, fileKeys);

        // null はクエリ失敗。原因が分かるようエラー文言を添える
        Assert.True(keys != null, "クエリ失敗: " + reader.LastSyobocalError);
    }
}
