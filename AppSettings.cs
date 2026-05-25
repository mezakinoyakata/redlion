using System.Text.Json;

namespace EDCBViewer;

public class AppSettings
{
    public string RecInfoPath { get; set; } = @"\\5600x\c\ap\edcb\Setting\RecInfo.txt";
    public string ReservePath { get; set; } = @"\\5600x\c\ap\edcb\Setting\Reserve.txt";
    public string RecordingFolder { get; set; } = @"\\5600x\d\PT2";
    public string PlayerPath { get; set; } = @"C:\ap\MPC-BE.1.8.1.x64\mpc-be64.exe";
    public string NotifyLogPath { get; set; } = @"\\5600x\c\ap\edcb\EpgTimerSrvNotify.log";
    public int RefreshIntervalSeconds { get; set; } = 60;

    // RecInfo.txt のパスはサーバー側のローカルパス (例: D:\PT2\foo.ts)
    // UNCパスに変換する: "D:\PT2\foo.ts" → "\\5600x\d\PT2\foo.ts"
    // サーバー名は RecInfoPath の UNC ホスト部分から取得する
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

    // RecInfoPath の UNC ホスト名を抽出 (\\5600x\... → "5600x")
    private string UncServer
    {
        get
        {
            if (RecInfoPath.StartsWith(@"\\"))
            {
                var parts = RecInfoPath.TrimStart('\\').Split('\\');
                if (parts.Length > 0) return parts[0];
            }
            return "";
        }
    }

    private static readonly string SettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

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
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
