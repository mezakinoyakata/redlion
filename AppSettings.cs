using System.Text.Json;

namespace EDCBViewer;

public class AppSettings
{
    /// <summary>EMWUI の URL。例: http://5600x:5510</summary>
    public string EmwuiBaseUrl { get; set; } = "";

    /// <summary>録画済み一覧の取得件数上限（新しい順に N 件）。</summary>
    public int MaxRecItems { get; set; } = 500;

    /// <summary>録画フォルダのパス（追っかけ再生時にファイルを探す）。例: \\5600x\d\PT2</summary>
    public string RecordingFolder { get; set; } = @"\\5600x\d\PT2";

    public string PlayerPath { get; set; } = @"C:\ap\MPC-BE.1.8.1.x64\mpc-be64.exe";
    public int RefreshIntervalSeconds { get; set; } = 60;

    /// <summary>再生サーバーのポート番号（ブラウザから /play?id=N で MPC-BE 起動）。</summary>
    public int PlayServerPort { get; set; } = 5580;

    /// <summary>エンコード済みフォルダのパス（ディレクトリタブで .mp4 を一覧表示）。</summary>
    public string EncodedFolder { get; set; } = "";

    /// <summary>EDCBのEPGデータフォルダ（*_epg.dat が置かれている場所）。</summary>
    public string EpgDataFolder { get; set; } = "";

    /// <summary>EpgData.db のフルパス（例: \\5600x\c\ap\edcb\Setting\EpgData.db）</summary>
    public string EpgDbPath { get; set; } = "";

    /// <summary>EPG MySQL 接続文字列（例: Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=pass）</summary>
    public string EpgMysqlConnectionString { get; set; } = "";

    // API が返すファイルパスはサーバー側のローカルパス (例: D:\PT2\foo.ts)
    // EmwuiBaseUrl のホスト名を使って UNC パスに変換する:
    //   "D:\PT2\foo.ts" + host "5600x" → "\\5600x\d\PT2\foo.ts"
    public string ToUncPath(string localPath)
    {
        if (string.IsNullOrEmpty(localPath))
            return localPath;

        // すでに UNC パスならそのまま返す
        if (localPath.StartsWith(@"\\"))
            return localPath;

        // ドライブレター形式 (X:\...) を UNC に変換
        if (localPath.Length >= 3 && localPath[1] == ':' && localPath[2] == '\\')
        {
            var server = UncServer;
            if (!string.IsNullOrEmpty(server))
            {
                var drive = char.ToLower(localPath[0]);
                var rest = localPath[3..]; // ドライブレターと "\" を除いた残り
                return $@"\\{server}\{drive}\{rest}";
            }
        }

        return localPath;
    }

    // EmwuiBaseUrl からホスト名を抽出 ("http://5600x:5510" → "5600x")
    private string UncServer
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(EmwuiBaseUrl))
            {
                try { return new Uri(EmwuiBaseUrl).Host; }
                catch { }
            }
            return "";
        }
    }

    private static readonly string SettingsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EDCBViewer",
            "settings.json");

    public static string ProgInfoCachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EDCBViewer",
            "proginfo_cache.json");

    public static string RecordingIndexPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EDCBViewer",
            "recording_index.db");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
