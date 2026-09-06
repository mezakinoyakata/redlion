using EDCBViewer.Services;

namespace EDCBViewer.Tests;

public class SyobocalTests
{
    // ─── XML パース ─────────────────────────────────────────────────────────

    private const string ProgXml = """
        <?xml version="1.0" encoding="UTF-8"?><ProgLookupResponse><ProgItems>
        <ProgItem id="1"><PID>100</PID><TID>7887</TID><StTime>2026-07-04 21:00:00</StTime>
        <EdTime>2026-07-04 21:30:00</EdTime><Count>1</Count><SubTitle>初陣</SubTitle>
        <Flag>1</Flag><Deleted>0</Deleted>
        <Warn>0</Warn><ChID>19</ChID><Revision>0</Revision></ProgItem>
        <ProgItem id="2"><PID>101</PID><TID>7887</TID><StTime>2026-07-09 22:30:00</StTime>
        <Count></Count><Flag>0</Flag><Deleted>0</Deleted><ChID>20</ChID></ProgItem>
        <ProgItem id="3"><PID>102</PID><TID>9999</TID><StTime>2026-07-05 00:00:00</StTime>
        <Count>3</Count><Flag>0</Flag><Deleted>1</Deleted><ChID>19</ChID></ProgItem>
        </ProgItems></ProgLookupResponse>
        """;

    [Fact]
    public void ParseProgs_ReadsFields()
    {
        var (rows, deleted) = SyobocalService.ParseProgs(ProgXml);
        Assert.Equal(2, rows.Count);
        var r = rows[0];
        Assert.Equal(100, r.PID);
        Assert.Equal(7887, r.TID);
        Assert.Equal(1, r.Count);
        Assert.Equal(19, r.ChID);
        Assert.Equal(new DateTime(2026, 7, 4, 21, 0, 0), r.StTime);
        Assert.Equal(new[] { 102 }, deleted);
    }

    [Fact]
    public void ParseProgs_EmptyCountBecomesNull()
    {
        var (rows, _) = SyobocalService.ParseProgs(ProgXml);
        Assert.Null(rows[1].Count);
    }

    private const string ChXml = """
        <?xml version="1.0" encoding="UTF-8"?><ChLookupResponse><ChItems>
        <ChItem id="19"><ChID>19</ChID><ChName>TOKYO MX</ChName><ChiEPGName>ＭＸテレビ</ChiEPGName><ChGID>1</ChGID></ChItem>
        <ChItem id="20"><ChID>20</ChID><ChName>AT-X</ChName><ChiEPGName>ＡＴ－Ｘ</ChiEPGName><ChGID>6</ChGID></ChItem>
        <ChItem id="239"><ChID>239</ChID><ChName>Abemaアニメ</ChName><ChiEPGName></ChiEPGName><ChGID>23</ChGID></ChItem>
        <ChItem id="1"><ChID>1</ChID><ChName>NHK総合</ChName><ChiEPGName>ＮＨＫ総合</ChiEPGName><ChGID>11</ChGID></ChItem>
        <ChItem id="30"><ChID>30</ChID><ChName>アニマックス</ChName><ChiEPGName>アニマックス</ChiEPGName><ChGID>6</ChGID></ChItem>
        <ChItem id="7"><ChID>7</ChID><ChName>テレビ東京</ChName><ChiEPGName>テレビ東京</ChiEPGName><ChGID>1</ChGID></ChItem>
        <ChItem id="4"><ChID>4</ChID><ChName>日本テレビ</ChName><ChiEPGName>日本テレビ</ChiEPGName><ChGID>1</ChGID></ChItem>
        </ChItems></ChLookupResponse>
        """;

    [Fact]
    public void ParseChannels_ReadsFields()
    {
        var chans = SyobocalService.ParseChannels(ChXml);
        Assert.Equal(7, chans.Count);
        Assert.Equal((19, "TOKYO MX", "ＭＸテレビ", 1), (chans[0].ChID, chans[0].Name, chans[0].EpgName, chans[0].Gid));
    }

    private const string TitleXml = """
        <?xml version="1.0" encoding="UTF-8"?><TitleLookupResponse><TitleItems>
        <TitleItem id="7887"><TID>7887</TID><Title>猫と竜</Title><FirstYear>2026</FirstYear><FirstMonth>6</FirstMonth></TitleItem>
        <TitleItem id="42"><TID>42</TID><Title>年月不明</Title><FirstYear></FirstYear><FirstMonth></FirstMonth></TitleItem>
        </TitleItems></TitleLookupResponse>
        """;

    [Fact]
    public void ParseTitleFirstYm_ReadsYearMonth()
    {
        var d = SyobocalService.ParseTitleFirstYm(TitleXml);
        Assert.Equal(202606, d[7887].FirstYm);
        Assert.Equal(0, d[42].FirstYm);
        Assert.Equal("猫と竜", d[7887].Title);
    }

    // ─── 話数タイトル（TitleLookup の SubTitles）─────────────────────────────

    [Fact]
    public void ParseSubTitles_ReadsEpisodeTitles()
    {
        // 実際の応答と同じ形式（*01*サブタイトル が1行1話）
        var d = SyobocalService.ParseSubTitles(
            "*01*猫と竜\n*02*母猫と少女\n*08*城下町の生活／黒猫と冒険王子\n");

        Assert.Equal(3, d.Count);
        Assert.Equal("猫と竜", d[1]);
        Assert.Equal("母猫と少女", d[2]);
        Assert.Equal("城下町の生活／黒猫と冒険王子", d[8]);   // スラッシュを含むタイトル
    }

    [Fact]
    public void ParseSubTitles_IgnoresJunkLines()
    {
        var d = SyobocalService.ParseSubTitles("見出し\n*03*第三話\n\n*あ*壊れた行\n");
        Assert.Single(d);
        Assert.Equal("第三話", d[3]);
    }

    [Fact]
    public void ParseSubTitles_EmptyStaysEmpty()
    {
        Assert.Empty(SyobocalService.ParseSubTitles(""));
        Assert.Empty(SyobocalService.ParseSubTitles("   \n "));
    }

    [Fact]
    public void ParseProgs_ReadsEndTimeAndSubTitle()
    {
        var (rows, _) = SyobocalService.ParseProgs(ProgXml);
        Assert.Equal(new DateTime(2026, 7, 4, 21, 30, 0), rows[0].EdTime);
        Assert.Equal("初陣", rows[0].SubTitle);
        Assert.Null(rows[1].EdTime);        // EdTime が無い放送もある
        Assert.Equal("", rows[1].SubTitle);
    }

    // ─── 作品解説の整形 ─────────────────────────────────────────────────────

    [Fact]
    public void CleanWiki_StripsMarkup()
    {
        var s = SyobocalService.CleanWiki(
            "*あらすじ\n''勇者''が[[魔王]]を倒す。\n\n\n*スタッフ\n監督：[[山田太郎|1234]]\n");

        Assert.Contains("あらすじ", s);
        Assert.Contains("勇者が魔王を倒す。", s);   // '' と [[ ]] が落ちている
        Assert.Contains("監督：山田太郎", s);       // [[表示|リンク先]] は表示側だけ残す
        Assert.DoesNotContain("[[", s);
        Assert.DoesNotContain("\n\n\n", s);         // 空行の連続はまとめる
    }

    [Fact]
    public void CleanWiki_FormatsStaffLines()
    {
        // 実際の応答（刃牙道）と同じ形。\r が生で混じり、スタッフは ":項目:値" 形式
        var s = SyobocalService.CleanWiki(
            "*リンク\r\n-公式 https://baki-anime.jp/\r\n\r\n*スタッフ\r\n" +
            ":原作:板垣恵介\r\n:監督:平野俊貴\r\n");

        Assert.DoesNotContain("\r", s);          // 改行が揃っている
        Assert.Contains("原作: 板垣恵介", s);    // ":項目:値" → "項目: 値"
        Assert.Contains("監督: 平野俊貴", s);
        Assert.Contains("・公式", s);            // "-項目" → 中黒
        Assert.Contains("スタッフ", s);
    }

    [Fact]
    public void CleanWiki_EmptyStaysEmpty()
    {
        Assert.Equal("", SyobocalService.CleanWiki(""));
        Assert.Equal("", SyobocalService.CleanWiki("   \n  \n"));
    }

    // ─── チャンネル対応付け ─────────────────────────────────────────────────

    private static List<SyobocalService.ChRow> Channels() =>
        SyobocalService.ParseChannels(ChXml);

    [Fact]
    public void StationMap_PrefixAndExactMatch()
    {
        var map = SyobocalService.BuildStationMap(
            ["ＴＯＫＹＯ　ＭＸ１", "ＡＴ－Ｘ", "ＮＨＫ総合１・東京", "ＢＳアニマックス", "ショップチャンネル"],
            Channels());

        Assert.Equal(new[] { 19 }, map["ＴＯＫＹＯ　ＭＸ１"]);  // "TOKYOMX1" 前方一致 "TOKYOMX"
        Assert.Equal(new[] { 20 }, map["ＡＴ－Ｘ"]);            // ハイフン除去で完全一致
        Assert.Equal(new[] { 1 },  map["ＮＨＫ総合１・東京"]);   // 前方一致 "NHK総合"
        Assert.Equal(new[] { 30 }, map["ＢＳアニマックス"]);     // 包含一致 "アニマックス"
        Assert.Empty(map["ショップチャンネル"]);            // 非アニメ局は対応なし
    }

    [Fact]
    public void StationMap_AbbreviatedStationsResolveViaAlias()
    {
        // 「テレ東」「日テレ１」は前方一致が成立しないためエイリアス表で対応
        var map = SyobocalService.BuildStationMap(["テレ東", "日テレ１"], Channels());
        Assert.Equal(new[] { 7 }, map["テレ東"]);
        Assert.Equal(new[] { 4 }, map["日テレ１"]);
    }

    [Fact]
    public void StationMap_ExcludesStreamingChannels()
    {
        // Abemaアニメ (ChGID=23) はどの局名にも対応しない
        var map = SyobocalService.BuildStationMap(["Ａｂｅｍａアニメ"], Channels());
        Assert.Empty(map["Ａｂｅｍａアニメ"]);
    }

    // ─── 取得チャンク（カバー範囲の連続性） ─────────────────────────────────

    [Fact]
    public void FetchChunks_InitialAscendingPairs()
    {
        var c = SyobocalService.BuildFetchChunks(0, 0, 202211, 202302);
        Assert.Equal([(202211, 202212), (202301, 202302)], c);
    }

    [Fact]
    public void FetchChunks_InitialOddCountEndsWithSingle()
    {
        var c = SyobocalService.BuildFetchChunks(0, 0, 202201, 202203);
        Assert.Equal([(202201, 202202), (202203, 202203)], c);
    }

    [Fact]
    public void FetchChunks_ExtendDownIsDescendingFromCoveredEdge()
    {
        // 途中で中断してもカバー範囲が連続で残るよう、既存範囲に近い側から取得する
        var c = SyobocalService.BuildFetchChunks(202303, 202304, 202212, 202304);
        Assert.Equal([(202301, 202302), (202212, 202212)], c);
    }

    [Fact]
    public void FetchChunks_ExtendUpIsAscending()
    {
        var c = SyobocalService.BuildFetchChunks(202301, 202302, 202301, 202305);
        Assert.Equal([(202303, 202304), (202305, 202305)], c);
    }

    [Fact]
    public void FetchChunks_FullyCoveredReturnsEmpty()
    {
        Assert.Empty(SyobocalService.BuildFetchChunks(202201, 202312, 202203, 202310));
    }

}

