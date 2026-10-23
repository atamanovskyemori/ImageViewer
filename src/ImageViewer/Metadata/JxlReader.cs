using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ImageViewer.Metadata;

/// <summary>JPEG XL から読んだ情報。読めなかったものは null</summary>
/// <param name="Exif">EXIF の TIFF 構造（"II*\0" / "MM\0*" から始まるバイト列）</param>
/// <param name="Width">表示するときの幅（向きを直した後）</param>
/// <param name="Height">表示するときの高さ（向きを直した後）</param>
public sealed record JxlInfo(byte[]? Exif, int? Width, int? Height);

/// <summary>
/// JPEG XL のメタデータを読む（書き込みはしない）。画像はデコードしない。
/// <list type="bullet">
/// <item>
/// 入れ物（ISOBMFF と同じ「箱」の並び。"JXL " の箱で始まる）なら、Exif の箱を読む。
/// 中身は HEIF と同じく「TIFF ヘッダまでの距離（4バイト）」の後に TIFF 構造が続く。
/// brob の箱（中身を Brotli で縮めた箱）に入っていることもある
/// </item>
/// <item>
/// 大きさは、画像の本体（コードストリーム。FF 0A で始まる）の先頭の SizeHeader から読む。
/// その後ろの ImageMetadata にある向き（orientation）が 90° 回転なら、縦横を入れ替える
/// </item>
/// </list>
/// 入れ物の無い JPEG XL（FF 0A で始まるもの）は EXIF を持てないので、大きさだけ。
/// 壊れたデータでも例外は投げず、読めた分だけ返す。
/// </summary>
public static class JxlReader
{
    // 壊れたファイルで延々と読まないための上限
    private const int MaxExifBytes = 16 * 1024 * 1024;
    private const int MaxBoxes = 10000;

    // SizeHeader と ImageMetadata の先頭（向き）を読むのに足りる長さ
    private const int CodestreamHeadBytes = 64;

    private static ReadOnlySpan<byte> ContainerSignature => [0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];

    private static ReadOnlySpan<byte> CodestreamSignature => [0xFF, 0x0A];

    // SizeHeader の ratio（1〜7）で決まる「幅 = 高さ × 分子 / 分母」
    private static readonly (int Num, int Den)[] Ratios = [(1, 1), (1, 1), (12, 10), (4, 3), (3, 2), (16, 9), (5, 4), (2, 1)];

    /// <summary>ファイル先頭が JPEG XL（入れ物、または入れ物の無いコードストリーム）か</summary>
    public static bool IsJxl(ReadOnlySpan<byte> header) =>
        header.StartsWith(ContainerSignature) || header.StartsWith(CodestreamSignature);

    /// <summary>JPEG XL を読む。JPEG XL でなければ null。stream はシークできること</summary>
    public static JxlInfo? Read(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            stream.Position = 0;
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            header = header[..read];

            if (header.StartsWith(CodestreamSignature))
            {
                // 入れ物が無い。EXIF は無く、大きさだけ
                stream.Position = 0;
                var (width, height) = ReadSize(ReadUpTo(stream, CodestreamHeadBytes));
                return new JxlInfo(null, width, height);
            }

            if (!header.StartsWith(ContainerSignature))
            {
                return null;
            }

            return ReadContainer(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>入れ物の箱を順に見て、Exif（brob で縮めたものも）と、コードストリームの先頭を探す</summary>
    private static JxlInfo ReadContainer(Stream stream)
    {
        byte[]? exif = null;
        int? width = null, height = null;
        long position = 0;
        Span<byte> boxHeader = stackalloc byte[16];

        for (int i = 0; i < MaxBoxes && position + 8 <= stream.Length; i++)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(boxHeader[..8], 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(boxHeader);
            int headerSize = 8;
            if (size == 1)
            {
                if (stream.ReadAtLeast(boxHeader[8..16], 8, throwOnEndOfStream: false) < 8)
                {
                    break;
                }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(boxHeader[8..16]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - position; // ファイルの最後まで
            }

            if (size < headerSize || size > stream.Length - position)
            {
                break; // 壊れている
            }

            long bodyStart = position + headerSize;
            long bodyLength = size - headerSize;
            var type = boxHeader[4..8];

            if (exif is null && type.SequenceEqual("Exif"u8) && bodyLength <= MaxExifBytes)
            {
                stream.Position = bodyStart;
                exif = HeifReader.TiffFromExifItem(ReadUpTo(stream, (int)bodyLength));
            }
            else if (exif is null && type.SequenceEqual("brob"u8) && bodyLength > 4 && bodyLength <= MaxExifBytes)
            {
                // Brotli で縮めた箱。先頭4バイトが元の箱の種類
                stream.Position = bodyStart;
                var body = ReadUpTo(stream, (int)bodyLength);
                if (body.AsSpan(0, 4).SequenceEqual("Exif"u8) && Decompress(body.AsSpan(4)) is { } payload)
                {
                    exif = HeifReader.TiffFromExifItem(payload);
                }
            }
            else if (width is null && (type.SequenceEqual("jxlc"u8) || type.SequenceEqual("jxlp"u8)))
            {
                // jxlc はコードストリームそのもの。jxlp は分割されたもので、先頭4バイトが通し番号
                long skip = type.SequenceEqual("jxlp"u8) ? 4 : 0;
                if (bodyLength > skip)
                {
                    stream.Position = bodyStart + skip;
                    (width, height) = ReadSize(ReadUpTo(stream, (int)Math.Min(CodestreamHeadBytes, bodyLength - skip)));
                }
            }

            position += size;
        }

        return new JxlInfo(exif, width, height);
    }

    /// <summary>Brotli を元に戻す。大きすぎる・壊れているなら null</summary>
    private static byte[]? Decompress(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxExifBytes)
                {
                    return null;
                }
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------
    // コードストリームの先頭（SizeHeader と、ImageMetadata の向き）
    // ------------------------------------------------------------

    /// <summary>
    /// コードストリーム（FF 0A で始まる）の先頭から、表示するときの大きさを読む。読めなければ (null, null)。
    /// ビットは下位ビットから順に詰めてある
    /// </summary>
    internal static (int? Width, int? Height) ReadSize(byte[] codestream)
    {
        if (!codestream.AsSpan().StartsWith(CodestreamSignature))
        {
            return (null, null);
        }

        var bits = new BitReader(codestream, 2);

        // SizeHeader
        bool small = bits.Read(1) == 1;
        long height = small ? (bits.Read(5) + 1) * 8 : ReadSizeU32(ref bits);
        int ratio = (int)bits.Read(3);
        long width = ratio == 0
            ? small ? (bits.Read(5) + 1) * 8 : ReadSizeU32(ref bits)
            : height * Ratios[ratio].Num / Ratios[ratio].Den;

        // ImageMetadata の先頭：すべて既定値でなく、追加の項目があれば、向き（1〜8）
        int orientation = 1;
        if (bits.Read(1) == 0 && bits.Read(1) == 1)
        {
            orientation = (int)bits.Read(3) + 1;
        }

        if (bits.Overrun || width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return (null, null);
        }

        // 5〜8 は 90° 回すので、縦横が入れ替わる
        return orientation >= 5 ? ((int)height, (int)width) : ((int)width, (int)height);
    }

    /// <summary>SizeHeader の大きさ：2ビットで4通りの長さ（9 / 13 / 18 / 30 ビット）を選び、1 を足す</summary>
    private static long ReadSizeU32(ref BitReader bits)
    {
        int bitCount = bits.Read(2) switch { 0 => 9, 1 => 13, 2 => 18, _ => 30 };
        return bits.Read(bitCount) + 1;
    }

    private static byte[] ReadUpTo(Stream stream, int length)
    {
        var buffer = new byte[length];
        int read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
        return read == length ? buffer : buffer[..read];
    }

    /// <summary>下位ビットから順に読む。足りなくなったら 0 を返して Overrun を立てる</summary>
    private struct BitReader(byte[] data, int startByte)
    {
        private long _bit = startByte * 8L;

        public bool Overrun { get; private set; }

        public long Read(int count)
        {
            long value = 0;
            for (int i = 0; i < count; i++, _bit++)
            {
                long index = _bit >> 3;
                if (index >= data.Length)
                {
                    Overrun = true;
                    return 0;
                }
                value |= (long)((data[index] >> (int)(_bit & 7)) & 1) << i;
            }
            return value;
        }
    }
}
