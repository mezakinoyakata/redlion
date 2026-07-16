using EDCBViewer.Services;

namespace EDCBViewer.Tests;

public class EpgTrigramTests
{
    // ─── HasCommonTrigram ──────────────────────────────────────────────────

    [Fact]
    public void SameString_HasCommonTrigram()
    {
        Assert.True(EpgDbReader.HasCommonTrigram("トライダーG7", "トライダーG7"));
    }

    [Fact]
    public void PartialOverlap_HasCommonTrigram()
    {
        // 「無敵ロボ トライダーG7」と「無敵ロボ トライダーG7 #31-#40」は共通3文字あり
        Assert.True(EpgDbReader.HasCommonTrigram("無敵ロボ トライダーG7", "無敵ロボ トライダーG7 #31-#40"));
    }

    [Fact]
    public void CompletelyUnrelated_NoCommonTrigram()
    {
        // トライダーG7 の event_name と カナン様の ext_text は無関係
        Assert.False(EpgDbReader.HasCommonTrigram(
            "無敵ロボ トライダーG7",
            "カナン様はあくまでチョロい　第11話"));
    }

    [Fact]
    public void ShortString_BelowTrigramLength_NoMatch()
    {
        // 2文字以下はトリグラムを生成できないので false
        Assert.False(EpgDbReader.HasCommonTrigram("ab", "abcdef"));
    }

    [Fact]
    public void ExactlyThreeChars_Matches()
    {
        Assert.True(EpgDbReader.HasCommonTrigram("abc", "xabcy"));
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        Assert.True(EpgDbReader.HasCommonTrigram("ABC", "xabcy"));
    }

    // ─── DB破損シナリオ再現 ────────────────────────────────────────────────
    // event_id=51767 AT-X: event_name=「無敵ロボ トライダーG7」だが
    // ext_text がカナン様の話数説明になっていた実際の破損ケース

    [Fact]
    public void CorruptedDb_EventNameVsDescription_NoCommonTrigram()
    {
        // event_name と ext_text の間にトリグラム一致がなければ null を返すべき
        const string eventName = "無敵ロボ トライダーG7";
        const string corruptedExt = "カナン様はあくまでチョロい　第11話「絶体絶命！ チョロいっていうな！」";
        Assert.False(EpgDbReader.HasCommonTrigram(eventName, corruptedExt));
        Assert.False(EpgDbReader.HasCommonTrigram(corruptedExt, eventName));
    }

    [Fact]
    public void HealthyDb_EventNameVsDescription_HasCommonTrigram()
    {
        // 正常データ: event_name と説明文が同じタイトルを含む
        const string eventName = "無敵ロボ トライダーG7";
        const string shortText = "無敵ロボ トライダーG7 第31話「鋼鉄の嵐！」";
        Assert.True(EpgDbReader.HasCommonTrigram(eventName, shortText) ||
                    EpgDbReader.HasCommonTrigram(shortText, eventName));
    }
}
