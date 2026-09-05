using System.Text.Json;

namespace EDCBViewer;

public class AppSettings
{
    /// <summary>録画済み一覧の取得件数上限（新しい順に N 件）。</summary>
    public int MaxRecItems { get; set; } = 500;

    /// <summary>録画フォルダのパス（追っかけ再生時にファイルを探す）。例: \\5600x\d\PT2</summary>
    public string RecordingFolder { get; set; } = @"\\5600x\d\PT2";

    public string PlayerPath { get; set; } = @"C:\ap\MPC-BE.1.8.1.x64\mpc-be64.exe";
    public int RefreshIntervalSeconds { get; set; } = 60;

    /// <summary>エンコード済みフォルダのパス（旧設定、EncodedFolders への移行用）。</summary>
    public string EncodedFolder { get; set; } = "";

    /// <summary>起点フォルダのリスト（ディレクトリタブでルートとして表示）。</summary>
    public List<string> EncodedFolders { get; set; } = new();

    /// <summary>
    /// PostgreSQL(Supabase) 接続文字列。
    /// 例: Host=5950X;Port=5432;Database=postgres;Username=postgres.xxx;Password=xxx;Timeout=10;
    /// Host は Supabase が動いているマシン名。127.0.0.1 はそのマシン自身でしか通らない。
    /// </summary>
    public string DbConnectionString { get; set; } = "";

    /// <summary>
    /// EDCB の EPG 蓄積ファイル(*_epg.dat)があるフォルダ。
    /// 起動時と「更新」時にここを読んで DB へ取り込む。空なら取り込みを行わない。
    /// </summary>
    public string EpgDataFolder { get; set; } = "";

    private static readonly string SettingsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EDCBViewer",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var result = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                // EncodedFolder → EncodedFolders への移行
                if (result.EncodedFolders.Count == 0 && !string.IsNullOrEmpty(result.EncodedFolder))
                    result.EncodedFolders.Add(result.EncodedFolder);
                return result;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
