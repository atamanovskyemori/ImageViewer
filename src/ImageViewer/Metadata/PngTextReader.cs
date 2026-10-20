using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ImageViewer.Metadata;

/// <summary>PNG のテキストチャンク1つ（キーワードと本文）</summary>
public sealed record PngText(string Keyword, string Text);

/// <summary>
/// PNG のテキストチャンク（tEXt / zTXt / iTXt）を全部読む。書き込みはしない。
/// <list type="bullet">
/// <item>tEXt：Latin-1 の文字列</item>
/// <item>zTXt：zlib で圧縮された Latin-1 の文字列</item>
/// <item>iTXt：UTF-8 の文字列（圧縮されていることもある）</item>
/// </list>
/// 画像データ（IDAT）の後ろに置くソフトもあるので、IEND まで読む（IDAT の中身は読まずに飛ばす）。
/// 壊れたチャンクは飛ばして、読めた分だけ返す。
/// </summary>
public static class PngTextReader
{
    // 壊れたファイルで延々と読まないための上限
    private const int MaxChunks = 10000;
    private const int MaxChunkBytes = 16 * 1024 * 1024;
    private const int MaxTextChars = 4 * 1024 * 1024;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>PNG でなければ空のリスト。stream はシークできること</summary>
    public static IReadOnlyList<PngText> Read(Stream stream)
    {
        var texts = new List<PngText>();
        try
        {
            stream.Position = 0;
            Span<byte> signature = stackalloc byte[8];
            if (!TryReadExactly(stream, signature)
                || !signature.SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                return texts;
            }

            Span<byte> header = stackalloc byte[8];
            for (int i = 0; i < MaxChunks; i++)
            {
                if (!TryReadExactly(stream, header))
                {
                    break;
                }

                uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                string type = Encoding.ASCII.GetString(header[4..8]);
                if (length > stream.Length - stream.Position)
                {
                    break; // 長さがファイルからはみ出している
                }

                if (type == "IEND")
                {
                    break;
                }

                if (type is "tEXt" or "zTXt" or "iTXt" && length <= MaxChunkBytes)
                {
                    var data = new byte[length];
                    if (!TryReadExactly(stream, data))
                    {
                        break;
                    }
                    stream.Seek(4, SeekOrigin.Current); // CRC

                    if (Parse(type, data) is { } text)
                    {
                        texts.Add(text);
                    }
                    continue;
                }

                stream.Seek(length + 4, SeekOrigin.Current); // データ + CRC
            }
        }
        catch (IOException)
        {
            // 読めたところまで返す
        }

        return texts;
    }

    /// <summary>チャンク1つの中身を読む。壊れていたら null</summary>
    public static PngText? Parse(string type, byte[] data)
    {
        int keywordEnd = Array.IndexOf(data, (byte)0);
        if (keywordEnd <= 0)
        {
            return null; // キーワードが無い
        }

        string keyword = Latin1.GetString(data, 0, keywordEnd);
        int pos = keywordEnd + 1;

        try
        {
            switch (type)
            {
                case "tEXt":
                    return new PngText(keyword, Limit(Latin1.GetString(data, pos, data.Length - pos)));

                case "zTXt":
                {
                    // 圧縮方式（0 = zlib）の1バイトの後ろが圧縮データ
                    if (pos >= data.Length || data[pos] != 0)
                    {
                        return null;
                    }
                    var inflated = Inflate(data.AsSpan(pos + 1));
                    return inflated is null ? null : new PngText(keyword, Limit(Latin1.GetString(inflated)));
                }

                case "iTXt":
                {
                    // 圧縮フラグ・圧縮方式・言語タグ\0・翻訳済みキーワード\0・本文（UTF-8）
                    if (pos + 2 > data.Length)
                    {
                        return null;
                    }
                    bool compressed = data[pos] == 1;
                    if (compressed && data[pos + 1] != 0)
                    {
                        return null;
                    }
                    pos += 2;

                    int languageEnd = Array.IndexOf(data, (byte)0, pos);
                    if (languageEnd < 0) return null;
                    int translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
                    if (translatedEnd < 0) return null;
                    pos = translatedEnd + 1;

                    byte[]? body = compressed ? Inflate(data.AsSpan(pos)) : data[pos..];
                    return body is null ? null : new PngText(keyword, Limit(Encoding.UTF8.GetString(body)));
                }

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>zlib で展開する。壊れている・大きすぎるときは null</summary>
    private static byte[]? Inflate(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            // 小さいデータが巨大に展開される（zip 爆弾）のを防ぐため、上限まで読んだら諦める
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxChunkBytes)
                {
                    return null;
                }
            }
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static string Limit(string text) => text.Length > MaxTextChars ? text[..MaxTextChars] : text;

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
