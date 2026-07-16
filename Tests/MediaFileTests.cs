using EDCBViewer.Models;

namespace EDCBViewer.Tests;

public class MediaFileTests
{
    // ─── helpers ───────────────────────────────────────────────────────────

    static MediaFile Make(string filenameWithoutExt)
        => new MediaFile { FilePath = @"C:\rec\" + filenameWithoutExt + ".ts" };

    // ─── 通常ファイル ───────────────────────────────────────────────────────

    [Fact]
    public void NormalFile_ParsesTitle()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)");
        Assert.Equal("無敵ロボ トライダーG7 #31-#40", f.ParsedTitle);
    }

    [Fact]
    public void NormalFile_ParsesStation()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)");
        Assert.Equal("ＡＴ－Ｘ", f.ParsedStation);
    }

    [Fact]
    public void NormalFile_ParsesStartTime()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)");
        Assert.Equal(new DateTime(2024, 1, 15, 1, 30, 0), f.ParsedStartTime);
    }

    [Fact]
    public void NormalFile_DisplayNameEqualsTitle()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)");
        Assert.Equal("無敵ロボ トライダーG7 #31-#40", f.DisplayName);
    }

    // ─── 継続録画ファイル -(N) ──────────────────────────────────────────────

    [Fact]
    public void ContinuationFile_ParsesTitleWithoutSuffix()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)-(1)");
        Assert.Equal("無敵ロボ トライダーG7 #31-#40", f.ParsedTitle);
    }

    [Fact]
    public void ContinuationFile_DisplayNameIncludesContinuationSuffix()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)-(1)");
        Assert.Equal("無敵ロボ トライダーG7 #31-#40 (続き1)", f.DisplayName);
    }

    [Fact]
    public void ContinuationFile_ParsesStation()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)-(1)");
        Assert.Equal("ＡＴ－Ｘ", f.ParsedStation);
    }

    [Fact]
    public void ContinuationFile_ParsesStartTime()
    {
        var f = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)-(1)");
        Assert.Equal(new DateTime(2024, 1, 15, 1, 30, 0), f.ParsedStartTime);
    }

    [Fact]
    public void ContinuationFile_ParsedTitleSameAsMain()
    {
        // 本編と継続ファイルの ParsedTitle が同一 → EPG クエリが同じキーで発行される
        var main = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)");
        var cont = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)-(1)");
        Assert.Equal(main.ParsedTitle, cont.ParsedTitle);
    }

    [Fact]
    public void ContinuationFile_DisplayNameDifferentFromMain()
    {
        // 重複しないよう DisplayName は異なる
        var main = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)");
        var cont = Make("無敵ロボ トライダーG7 #31-#40 (ＡＴ－Ｘ 2024-01-15-0130-月)-(1)");
        Assert.NotEqual(main.DisplayName, cont.DisplayName);
    }

    // ─── パターン不一致 ────────────────────────────────────────────────────

    [Fact]
    public void UnmatchedFilename_FallsBackToFilename()
    {
        var f = Make("some_random_video");
        Assert.Equal("some_random_video", f.ParsedTitle);
        Assert.Equal("", f.ParsedStation);
        Assert.Null(f.ParsedStartTime);
    }

    [Fact]
    public void UnmatchedFilename_DisplayNameEqualsTitle()
    {
        var f = Make("some_random_video");
        Assert.Equal("some_random_video", f.DisplayName);
    }

    // ─── ディレクトリ ──────────────────────────────────────────────────────

    [Fact]
    public void Directory_DisplayNameHasFolderPrefix()
    {
        var f = new MediaFile { FilePath = @"C:\rec\某フォルダ", IsDirectory = true };
        Assert.StartsWith("📁 ", f.DisplayName);
        Assert.Contains("某フォルダ", f.DisplayName);
    }

    // ─── 検索用正規化（全角半角無視） ────────────────────────────────────────

    [Fact]
    public void NormalizeForSearch_FoldsFullWidthAlphanumerics()
    {
        Assert.Equal("4K", MediaFile.NormalizeForSearch("４Ｋ"));
        Assert.Equal("HDR", MediaFile.NormalizeForSearch("ＨＤＲ"));
    }

    [Fact]
    public void SearchText_MatchesHalfWidthTermAgainstFullWidthFilename()
    {
        // 「4K」（半角）で「ＮＨＫ　ＢＳＰ４Ｋ」（全角）の局名がヒットする
        var f = Make("【大河ドラマアンコール】太平記（１２）「笠置落城」 (ＮＨＫ　ＢＳＰ４Ｋ 2026-07-05-1130-日)");
        var term = MediaFile.NormalizeForSearch("4K");
        Assert.Contains(term, f.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchText_MatchesCaseInsensitively()
    {
        var f = Make("鋼の錬金術師 4K Ultra HD 特別放送 (ＢＳ１１イレブン 2025-12-27-2330-土)");
        var term = MediaFile.NormalizeForSearch("ultra hd");
        Assert.Contains(term, f.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    // ─── タイムスタンプ ────────────────────────────────────────────────────

    [Fact]
    public void ParsedStartTimeText_FormattedCorrectly()
    {
        var f = Make("番組タイトル (テレビ東京 2025-12-31-2355-水)");
        Assert.Equal("2025/12/31 23:55", f.ParsedStartTimeText);
    }

    [Fact]
    public void ParsedStartTimeText_EmptyWhenNoTime()
    {
        var f = Make("some_random_video");
        Assert.Equal("", f.ParsedStartTimeText);
    }
}
