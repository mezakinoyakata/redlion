namespace EDCBViewer.Tests;

/// <summary>
/// 実 DB に接続するテスト用の接続先。
///
/// このリポジトリは公開しているので、接続文字列（パスワードを含む）をソースに書かない。
/// EDCBViewer 本体と同じ settings.json から読む。
/// 設定されていない環境では空文字になり、各テストは何も検証せずに抜ける。
/// </summary>
internal static class TestDb
{
    public static string ConnStr => AppSettings.Load().DbConnectionString;

    public static bool Available()
    {
        if (string.IsNullOrWhiteSpace(ConnStr)) return false;
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(ConnStr);
            conn.Open();
            return true;
        }
        catch { return false; }
    }
}
