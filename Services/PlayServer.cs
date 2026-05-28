using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using EDCBViewer.Parsers;

namespace EDCBViewer.Services;

/// <summary>
/// ブラウザ（EMWUI）から MPC-BE を起動させるためのローカル HTTP サーバー。
/// GET /play?id=録画ID  → EMWUI API で recFilePath を解決し MPC-BE で開く
/// GET /play?path=...   → パス直接指定（URL エンコード済み）
/// GET /ping            → 死活確認 {"ok":true}
/// </summary>
public sealed class PlayServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource _cts = new();
    private AppSettings _settings;

    public int Port { get; }

    public PlayServer(int port, AppSettings settings)
    {
        Port = port;
        _settings = settings;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    /// <summary>設定が変わったときに差し替える。</summary>
    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => ListenAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    public void Dispose() => Stop();

    // ── 受信ループ ────────────────────────────────────────────────────────────

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch { break; }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // CORS: EMWUI は異なるオリジン (5600x:5510) からアクセスしてくる
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "*");

        try
        {
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                return;
            }

            var path = req.Url?.AbsolutePath ?? "";

            if (path == "/ping")
            {
                await WriteJson(res, 200, """{"ok":true}""");
                return;
            }

            if (path == "/play")
            {
                var query = ParseQuery(req.Url?.Query ?? "");
                string? filePath = null;

                // id= で指定された場合は EMWUI API から recFilePath を取得
                if (query.TryGetValue("id", out var idStr) && uint.TryParse(idStr, out var recId))
                    filePath = await FetchPathByIdAsync(recId);
                // path= で直接指定された場合
                else if (query.TryGetValue("path", out var rawPath))
                    filePath = Uri.UnescapeDataString(rawPath);

                if (filePath == null)
                {
                    await WriteJson(res, 400, """{"err":"録画が見つかりません"}""");
                    return;
                }

                var uncPath = _settings.ToUncPath(filePath);
                var (ok, err) = LaunchPlayer(uncPath);

                await WriteJson(res, ok ? 200 : 500,
                    ok ? """{"ok":true}""" : $$$"""{"err":"{{{err}}}"}""");
                return;
            }

            res.StatusCode = 404;
        }
        catch (Exception ex)
        {
            try { await WriteJson(res, 500, $"{{\"err\":\"{ex.Message}\"}}"); } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    // ── EpgTimerSrv API で recFilePath を解決 ────────────────────────────────

    private async Task<string?> FetchPathByIdAsync(uint id)
    {
        var baseUrl = _settings.EmwuiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        try
        {
            return await new EmwuiClient(baseUrl).GetRecFilePathAsync(id);
        }
        catch { }
        return null;
    }

    // ── MPC-BE 起動 ───────────────────────────────────────────────────────────

    private (bool ok, string err) LaunchPlayer(string filePath)
    {
        var player = _settings.PlayerPath;
        if (!File.Exists(player))
            return (false, $"MPC-BE が見つかりません: {player}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = player,
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false,
            });
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── ユーティリティ ────────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var sep = part.IndexOf('=');
            if (sep > 0)
                d[Uri.UnescapeDataString(part[..sep])] = Uri.UnescapeDataString(part[(sep + 1)..]);
        }
        return d;
    }

    private static async Task WriteJson(HttpListenerResponse res, int status, string json)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }
}
