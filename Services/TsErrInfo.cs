using System.Text.RegularExpressions;

namespace EDCBViewer.Services;

/// <summary>
/// EDCB が録画ファイルの隣に書き出す "&lt;録画ファイル名&gt;.err" からドロップ／スクランブル数を読む。
///
/// MySQL の recordings テーブルは存在せず（recordings_old は 2026-06 で更新停止）、
/// EDCBViewer は DB へ書き込まない方針のため、常に最新である .err を一次ソースとする。
///
/// 書式（PID 行が並び、末尾に使用 BonDriver 名）:
///   PID: 0x100F  Total:  7970054  Drop:        0  Scramble:         0  MPEG2 VIDEO
/// 数値部は ASCII なので、文字コードに依存しないよう Latin1 で読む。
/// </summary>
public static class TsErrInfo
{
    public sealed record Counts(long Drops, long Scrambles);

    private static readonly Regex LineRegex = new(
        @"Drop:\s*(-?\d+)\s+Scramble:\s*(-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>録画ファイルのパスから .err を探して合計を返す。無い・読めない場合は null。</summary>
    public static Counts? TryRead(string recordingFilePath)
    {
        if (string.IsNullOrEmpty(recordingFilePath)) return null;
        try
        {
            var errPath = recordingFilePath + ".err";
            if (!File.Exists(errPath)) return null;

            long drops = 0, scrambles = 0;
            var matched = false;
            foreach (var line in File.ReadLines(errPath, System.Text.Encoding.Latin1))
            {
                var m = LineRegex.Match(line);
                if (!m.Success) continue;
                // PID ごとの行を合算する（オーバーフローは実運用上ありえないが念のため飽和させない）
                drops     += long.Parse(m.Groups[1].Value);
                scrambles += long.Parse(m.Groups[2].Value);
                matched = true;
            }
            return matched ? new Counts(drops, scrambles) : null;
        }
        catch
        {
            // ネットワーク越しの録画フォルダが一時的に落ちている等。表示を諦めるだけでよい
            return null;
        }
    }

    /// <summary>右ペイン表示用の "ドロップ / スクランブル" 文字列。読めなければ空文字。</summary>
    public static string Format(string recordingFilePath)
    {
        var c = TryRead(recordingFilePath);
        return c == null ? "" : $"{c.Drops} / {c.Scrambles}";
    }
}
