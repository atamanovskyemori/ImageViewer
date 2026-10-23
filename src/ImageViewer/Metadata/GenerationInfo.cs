using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageViewer.Metadata;

/// <summary>生成情報をどこから読んだか（<see cref="GenerationInfo.Source"/>）</summary>
public static class GenerationSources
{
    public const string PngParameters = "PNG（parameters）";
    public const string ExifUserComment = "EXIF（UserComment）";
    public const string PngComfyUI = "PNG（ComfyUI の prompt）";
    public const string ExifComfyUI = "EXIF（ComfyUI の prompt）";
}

/// <summary>
/// 画像生成AIが画像に書き込んだ生成情報（プロンプトなど）。
/// Stable Diffusion WebUI（AUTOMATIC1111 / Forge など）の書き方を読む。
/// ComfyUI のノード構成からも、同じ形に取り出せる（<see cref="ParseComfyUI"/>。GenerationInfo.ComfyUI.cs）。
/// <code>
/// （プロンプト。複数行のこともある）
/// Negative prompt: （ネガティブプロンプト。複数行のこともある）
/// Steps: 20, Sampler: Euler a, CFG scale: 7, Seed: 123, Size: 512x768, Model: xxx, ...
/// </code>
/// 書き込まれている場所は、PNG ならテキストチャンク "parameters"、JPEG / WebP なら EXIF の UserComment。
/// </summary>
public sealed partial class GenerationInfo
{
    // WebUI 本体の parse_generation_parameters と同じ読み方：「キー: 値」が , 区切りで並ぶ。値は "..." で囲まれていることもある
    [GeneratedRegex("""\s*(\w[\w \-/]+):\s*("(?:\\.|[^\\"])+"|[^,]*)(?:,|$)""")]
    private static partial Regex ParameterPattern();

    // 最後の行を「設定の行」とみなすのに必要な「キー: 値」の数（WebUI 本体と同じ）
    private const int MinParametersInLastLine = 3;

    private GenerationInfo(string source, string? prompt, string? negativePrompt, IReadOnlyList<KeyValuePair<string, string>> parameters, string raw)
    {
        Source = source;
        Prompt = prompt;
        NegativePrompt = negativePrompt;
        Parameters = parameters;
        Raw = raw;
    }

    /// <summary>どこから読んだか（<see cref="GenerationSources"/> のどれか）</summary>
    public string Source { get; }

    public string? Prompt { get; }

    public string? NegativePrompt { get; }

    /// <summary>設定の行の「キー: 値」（書かれていた順）</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; }

    /// <summary>書き込まれていた文字列そのもの</summary>
    public string Raw { get; }

    /// <summary>
    /// WebUI の書き方として読む。設定の行が無くても、文字列全体をプロンプトとして返す（WebUI はプロンプトだけのこともある）。
    /// 空なら null。
    /// </summary>
    public static GenerationInfo? Parse(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var raw = text.Replace("\r\n", "\n").Trim();
        var lines = raw.Split('\n').ToList();

        // 最後の行が「Steps: 20, Sampler: ...」なら、設定の行として外す
        var parameters = new List<KeyValuePair<string, string>>();
        var matches = ParameterPattern().Matches(lines[^1]);
        if (matches.Count >= MinParametersInLastLine)
        {
            foreach (Match match in matches)
            {
                parameters.Add(new(match.Groups[1].Value.Trim(), Unquote(match.Groups[2].Value.Trim())));
            }
            lines.RemoveAt(lines.Count - 1);
        }

        var prompt = new StringBuilder();
        var negative = new StringBuilder();
        bool inNegative = false;
        foreach (var line in lines)
        {
            const string NegativeLabel = "Negative prompt:";
            if (!inNegative && line.StartsWith(NegativeLabel, StringComparison.Ordinal))
            {
                inNegative = true;
                negative.Append(line[NegativeLabel.Length..].Trim());
            }
            else if (inNegative)
            {
                negative.Append('\n').Append(line);
            }
            else
            {
                if (prompt.Length > 0) prompt.Append('\n');
                prompt.Append(line);
            }
        }

        return new GenerationInfo(
            source,
            NullIfBlank(prompt.ToString()),
            NullIfBlank(negative.ToString()),
            parameters,
            raw);
    }

    /// <summary>設定の値を取り出す（キーの大文字小文字は区別しない）</summary>
    public string? Get(string key) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// "..." で囲まれた値は JSON の文字列として外す（WebUI は , を含む値をこう書く）。読めなければそのまま
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? value;
            }
            catch (JsonException)
            {
                return value[1..^1];
            }
        }
        return value;
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ------------------------------------------------------------
    // EXIF の UserComment
    // ------------------------------------------------------------

    /// <summary>
    /// EXIF の UserComment を文字列にする。先頭8バイトが文字コードの指定
    /// （"UNICODE\0" = UTF-16、"ASCII\0\0\0"、8バイトとも 0 = 指定なし）。
    /// UTF-16 のバイト順は決まっていないので（WebUI はビッグエンディアンで書く）、中身を見て判断する。
    /// </summary>
    public static string? DecodeUserComment(byte[]? bytes)
    {
        if (bytes is null || bytes.Length <= 8)
        {
            return null;
        }

        var header = bytes.AsSpan(0, 8);
        var body = bytes.AsSpan(8);
        string text;

        if (header.SequenceEqual("UNICODE\0"u8))
        {
            text = DecodeUtf16(body);
        }
        else if (header.SequenceEqual("JIS\0\0\0\0\0"u8))
        {
            return null; // JIS は今は読まない（使っているソフトがほぼ無い）
        }
        else
        {
            // ASCII / 指定なし。実際には UTF-8 や Shift_JIS で書くソフトもあるので、ExifText で読む
            text = ExifText.Decode(body);
        }

        text = text.TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 2)
        {
            // BOM があればそれに従う
            if (body[0] == 0xFE && body[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(body[2..]);
            if (body[0] == 0xFF && body[1] == 0xFE) return Encoding.Unicode.GetString(body[2..]);
        }

        // BOM が無ければ、英数字の上位バイト（0）が偶数番目と奇数番目のどちらに多いかで判断する
        int evenZeros = 0, oddZeros = 0;
        for (int i = 0; i + 1 < body.Length && i < 4096; i += 2)
        {
            if (body[i] == 0) evenZeros++;
            if (body[i + 1] == 0) oddZeros++;
        }

        return evenZeros >= oddZeros
            ? Encoding.BigEndianUnicode.GetString(body)
            : Encoding.Unicode.GetString(body);
    }
}
