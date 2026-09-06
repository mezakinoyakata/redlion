using System.IO;
using EDCBViewer.Models;
using EDCBViewer.Services;

namespace EDCBViewer.Tests;

/// <summary>
/// 最速マークが画面に出ない件の切り分け。
/// 実際の録画フォルダを読んで、アプリと同じ手順で判定を走らせる。
/// フォルダか DB に届かない環境では何も検証せずに抜ける。
/// </summary>
public class FastestMarkTests
{
    private static List<(string Station, DateTime Time)> FileKeys()
    {
        var keys = new List<(string, DateTime)>();
        foreach (var dir in AppSettings.Load().EncodedFolders)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var f = new MediaFile { FilePath = path };
                if (f.ParsedStartTime is { } t && !string.IsNullOrEmpty(f.ParsedStation))
                    keys.Add((f.ParsedStation, t));
            }
        }
        return keys.Distinct().ToList();
    }

    /// <summary>
    /// EPG の蓄積開始日時に合成行を数えてはいけない。
    /// 数えると全ファイルが「EPG のある期間」と判定され、最速判定が
    /// 12 年分を一度に処理して時間切れになる。
    /// </summary>
    [Fact]
    public void EPG蓄積開始に合成行を数えない()
    {
        if (!TestDb.Available()) return;

        var reader = new EpgDbReader(TestDb.ConnStr);
        var min = reader.GetEventsMinStartTime();
        if (min == null) return;

        using var conn = new Npgsql.NpgsqlConnection(TestDb.ConnStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT MIN(start_time) FROM events WHERE event_id < {EpgDbReader.SyntheticEventIdBase}";
        var realMin = cmd.ExecuteScalar() as DateTime?;
        if (realMin == null) return;

        Assert.Equal(realMin.Value, min.Value);
    }

    [Fact]
    public void 実ファイルで最速判定が結果を返す()
    {
        if (!TestDb.Available()) return;
        var fileKeys = FileKeys();
        if (fileKeys.Count == 0) return;   // 共有に届かない環境

        var reader = new EpgDbReader(TestDb.ConnStr);
        var eventsMin = reader.GetEventsMinStartTime();
        var (coveredFrom, _, _) = reader.GetSyobocalMeta();
        Assert.NotNull(eventsMin);
        Assert.NotEqual(0, coveredFrom);

        // events のある期間（アプリの CollectFastestKeys と同じ分け方）
        var withEvents = fileKeys.Where(k => k.Time >= eventsMin!.Value).ToList();
        Assert.NotEmpty(withEvents);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var keys = reader.GetFastestKeysViaJoin(
            eventsMin!.Value, DateTime.Now, coveredFrom, withEvents);
        sw.Stop();

        Assert.True(keys != null,
            $"判定が失敗した（対象 {withEvents.Count} 件 / 全ファイル {fileKeys.Count} 件 / " +
            $"{sw.Elapsed.TotalSeconds:F0} 秒）: {reader.LastSyobocalError}");
        Assert.True(keys!.Count > 0,
            $"最速が0件。対象ファイル {withEvents.Count} 件 / covered_from {coveredFrom}");
    }
}
