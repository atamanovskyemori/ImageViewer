using System.Text;

namespace ImageViewer.Metadata;

/// <summary>
/// EXIF の文字列（ASCII 型のタグ・UserComment）を読む。
/// 仕様では ASCII だが、実際には UTF-8 で書くソフトも、Shift_JIS で書く日本のカメラ・ソフトもある。
/// どれでも文字化け（「�」）しないよう、順に試す。
/// <list type="number">
/// <item>UTF-8 として正しく読めれば、それ（ASCII もここで読める）</item>
/// <item>最後だけ読めない（決まった長さの欄に収めるために、文字の途中で切られた）なら、そこだけ落とす</item>
/// <item>Shift_JIS として正しく読めれば、それ</item>
/// <item>どれでもなければ、UTF-8 として読めるところだけ読む（読めない文字は「�」になる）</item>
/// </list>
/// </summary>
public static class ExifText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding? StrictShiftJis = CreateStrictShiftJis();

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // UTF-8 ではない（か、途中で切れている）
        }

        var lenient = Encoding.UTF8.GetString(bytes);
        int firstBad = lenient.IndexOf('�');
        if (firstBad > 0 && lenient.AsSpan(firstBad).Trim('�').IsEmpty)
        {
            return lenient[..firstBad]; // 最後の文字が途中で切れていただけ
        }

        if (StrictShiftJis is not null)
        {
            try
            {
                return StrictShiftJis.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Shift_JIS でもない
            }
        }

        return lenient;
    }

    private static Encoding? CreateStrictShiftJis()
    {
        try
        {
            // .NET では、Shift_JIS などの古い文字コードは登録しないと使えない
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
