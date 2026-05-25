using System.Text;

namespace EDCBViewer.Parsers;

public static class EncodingDetector
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];

    // 読み込み済みバイト列からエンコーディングを判定する（ファイルを開かない）
    public static Encoding DetectFromBytes(byte[] raw)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (raw.Length >= 3 && raw[0] == Utf8Bom[0] && raw[1] == Utf8Bom[1] && raw[2] == Utf8Bom[2])
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        if (raw.Length >= 2 && raw[0] == Utf16LeBom[0] && raw[1] == Utf16LeBom[1])
            return Encoding.Unicode;

        return Encoding.GetEncoding(932);
    }
}
