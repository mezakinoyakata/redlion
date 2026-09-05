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
