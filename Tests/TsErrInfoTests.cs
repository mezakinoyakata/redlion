using System.IO;
using EDCBViewer.Services;

namespace EDCBViewer.Tests;

public class TsErrInfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TsErrInfoTests_" + Guid.NewGuid().ToString("N"));

    public TsErrInfoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>録画ファイル "&lt;name&gt;.ts" の隣に "&lt;name&gt;.ts.err" を作り、録画ファイルのパスを返す。</summary>
    private string WriteErr(string name, string content)
    {
        var tsPath = Path.Combine(_dir, name + ".ts");
        File.WriteAllText(tsPath + ".err", content, System.Text.Encoding.UTF8);
        return tsPath;
    }

    // EDCB が実際に書き出す形式（PID 行が並び、末尾に使用 BonDriver 名）
    private const string RealSample =
        "PID: 0x0000  Total:    18077  Drop:        0  Scramble:         0  PAT\r\n" +
        "PID: 0x0012  Total:   496826  Drop:        0  Scramble:         0  EIT\r\n" +
        "PID: 0x100F  Total:  7970054  Drop:        0  Scramble:         0  MPEG2 VIDEO\r\n" +
        "使用BonDriver : BonDriver_Spinel_PT-S0.dll\r\n";

    [Fact]
    public void TryRead_CleanRecording_ReturnsZeros()
    {
        var ts = WriteErr("clean", RealSample);
        var c = TsErrInfo.TryRead(ts);
        Assert.NotNull(c);
        Assert.Equal(0, c!.Drops);
        Assert.Equal(0, c.Scrambles);
        Assert.Equal("0 / 0", TsErrInfo.Format(ts));
    }

    [Fact]
    public void TryRead_SumsAcrossAllPids()
    {
        var ts = WriteErr("dropped",
            "PID: 0x0000  Total:    18077  Drop:        3  Scramble:         0  PAT\r\n" +
            "PID: 0x100F  Total:  7970054  Drop:       12  Scramble:         7  MPEG2 VIDEO\r\n" +
            "PID: 0x104F  Total:   323190  Drop:        0  Scramble:         5  MPEG2 AAC\r\n" +
            "使用BonDriver : BonDriver_Spinel_PT-S0.dll\r\n");
        var c = TsErrInfo.TryRead(ts);
        Assert.NotNull(c);
        Assert.Equal(15, c!.Drops);
        Assert.Equal(12, c.Scrambles);
        Assert.Equal("15 / 12", TsErrInfo.Format(ts));
    }

    [Fact]
    public void TryRead_MissingErrFile_ReturnsNull()
    {
        var ts = Path.Combine(_dir, "no_such_recording.ts");
        Assert.Null(TsErrInfo.TryRead(ts));
        Assert.Equal("", TsErrInfo.Format(ts));
    }

    [Fact]
    public void TryRead_ErrFileWithoutPidLines_ReturnsNull()
    {
        // 中身が空、あるいは PID 行が1つも無い場合は「0件」ではなく「不明」として扱う
        var ts = WriteErr("empty", "使用BonDriver : BonDriver_Spinel_PT-S0.dll\r\n");
        Assert.Null(TsErrInfo.TryRead(ts));
        Assert.Equal("", TsErrInfo.Format(ts));
    }

    [Fact]
    public void TryRead_EmptyPath_ReturnsNull()
    {
        Assert.Null(TsErrInfo.TryRead(""));
        Assert.Equal("", TsErrInfo.Format(""));
    }
}
