using System.IO;
using EDCBViewer.Services;

namespace EDCBViewer.Tests;

/// <summary>
/// 起動時・更新時の EPG 取り込み。
/// EpgDataCap3.dll（ネイティブ）の呼び出しを含むので、DLL が実際に読めるかもここで分かる。
/// </summary>
public class EpgImporterTests
{
    private const string EpgDir = @"C:\ap\edcb\Setting\EpgData";

    // 接続先は settings.json から読む（TestDb 参照。ソースには書かない）
    private static string ConnStr => TestDb.ConnStr;

    [Fact]
    public void フォルダ未設定なら何もしない()
    {
        var r = EpgImporter.Run(ConnStr, "");
        Assert.False(r.Ok);
        Assert.Contains("未設定", r.Message);
    }

    [Fact]
    public void フォルダが無ければ失敗を返す()
    {
        var r = EpgImporter.Run(ConnStr, @"C:\存在しないフォルダ\EpgData");
        Assert.False(r.Ok);
        Assert.Contains("ありません", r.Message);
    }

    [Fact]
    public void DLLで_epg_datから番組を取り出せる()
    {
        if (!Directory.Exists(EpgDir)) return;

        using var epg = new EpgDataCap3();
        foreach (var f in Directory.GetFiles(EpgDir, "*_epg.dat")) epg.LoadFile(f);

        var services = epg.GetServices();
        Assert.NotEmpty(services);

        var events = services.SelectMany(epg.GetEvents).ToList();
        Assert.NotEmpty(events);
        // ARIB 8単位符号が復号できていること（できていないと化けるか空になる）
        Assert.Contains(events, e => e.EventName.Length > 0 && e.StartTime != null);
    }

    /// <summary>
    /// 取り込みを最後まで通す。UPSERT なので何度流しても結果は同じ。
    /// DB か EPG フォルダが無い環境では何も検証せずに抜ける。
    /// </summary>
    [Fact]
    public void 取り込みを最後まで実行できる()
    {
        if (!Directory.Exists(EpgDir) || !TestDb.Available()) return;

        var log = new List<string>();
        var r = EpgImporter.Run(ConnStr, EpgDir, log.Add);

        Assert.True(r.Ok, r.Message);
        Assert.Contains("完了", r.Message);
        Assert.NotEmpty(log);   // 進捗が呼ばれている（ステータスバー表示用）
    }
}
