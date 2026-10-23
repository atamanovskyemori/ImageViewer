using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ImageViewer.Metadata;

/// <summary>
/// EXIF を読む（書き込みはしない）。
/// EXIF の中身は TIFF 形式（IFD とタグの並び）なので、入れ物ごとにその部分を探して、同じ処理で読む。
/// <list type="bullet">
/// <item>JPEG：APP1 セグメント（"Exif\0\0" の後ろ）</item>
/// <item>TIFF：ファイル全体が TIFF</item>
/// <item>PNG：eXIf チャンク</item>
/// <item>WebP：EXIF チャンク</item>
/// <item>HEIF（HEIC / AVIF）：meta 箱の中の Exif アイテム（<see cref="HeifReader"/>）</item>
/// <item>JPEG XL：入れ物の Exif の箱（Brotli で縮めた brob の箱も。<see cref="JxlReader"/>）</item>
/// </list>
/// 壊れたデータ（範囲外のオフセット、ループする IFD、ありえない個数）でも例外は投げず、読めた分だけ返す。
/// 読むのは IFD0・Exif IFD・GPS IFD だけ（サムネイル用の IFD1 やメーカー独自の情報は読まない）。
/// </summary>
public static class ExifReader
{
    // 壊れたファイルで延々と読まないための上限
    private const int MaxEntriesPerIfd = 1000;
    private const int MaxValueBytes = 64 * 1024;
    private const int MaxBlockBytes = 16 * 1024 * 1024;
    private const int MaxSegments = 10000;

    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();

    /// <summary>
    /// 画像ファイルから EXIF を読む。stream はシークできること。EXIF が無い・対応していない形式なら null。
    /// </summary>
    public static ExifData? Read(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("シークできるストリームが必要です。", nameof(stream));
        }

        try
        {
            Span<byte> header = stackalloc byte[12];
            stream.Position = 0;
            if (!TryReadExactly(stream, header))
            {
                return null;
            }

            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                return ReadBlock(FindJpegExif(stream));
            }

            if (header[..8].SequenceEqual(PngSignature))
            {
                return ReadBlock(FindPngExif(stream));
            }

            if (header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            {
                return ReadBlock(FindWebPExif(stream));
            }

            if (HeifReader.IsHeif(header))
            {
                return ReadBlock(HeifReader.Read(stream)?.Exif);
            }

            if (JxlReader.IsJxl(header))
            {
                return ReadBlock(JxlReader.Read(stream)?.Exif);
            }

            if (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8))
            {
                // TIFF はファイル全体が TIFF 構造。IFD がファイルのどこにあってもいいように、全部は読まずにシークして読む
                return ReadTiff(stream, 0, stream.Length);
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>TIFF 構造（"II*\0" / "MM\0*" で始まるバイト列）から EXIF を読む</summary>
    public static ExifData? ReadTiff(byte[] tiff)
    {
        using var stream = new MemoryStream(tiff, writable: false);
        return ReadTiff(stream, 0, tiff.Length);
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ExifData? ReadBlock(byte[]? block) => block is null ? null : ReadTiff(block);

    // ------------------------------------------------------------
    // 入れ物から TIFF 部分を探す
    // ------------------------------------------------------------

    /// <summary>JPEG のセグメントを順に見て、Exif の APP1 を探す。画像データ（SOS）まで来たら諦める</summary>
    private static byte[]? FindJpegExif(Stream stream)
    {
        stream.Position = 2;
        Span<byte> lengthBytes = stackalloc byte[2];

        for (int i = 0; i < MaxSegments; i++)
        {
            if (stream.ReadByte() != 0xFF)
            {
                return null; // セグメントの区切りが無い（壊れている）
            }

            int marker;
            do
            {
                marker = stream.ReadByte(); // 0xFF が続くのは詰め物
            }
            while (marker == 0xFF);

            if (marker < 0 || marker == 0xD9 || marker == 0xDA)
            {
                return null; // ファイル末尾 / EOI / SOS（ここから先は画像データ）
            }

            if (marker is (>= 0xD0 and <= 0xD7) or 0x01)
            {
                continue; // 長さを持たないマーカー
            }

            if (!TryReadExactly(stream, lengthBytes))
            {
                return null;
            }

            int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes) - 2;
            if (payloadLength < 0)
            {
                return null;
            }

            if (marker == 0xE1 && payloadLength >= ExifHeader.Length)
            {
                if (payloadLength > MaxBlockBytes)
                {
                    if (!stream.CanSeek)
                    {
                        return null;
                    }

                    stream.Seek(payloadLength, SeekOrigin.Current);
                    continue;
                }

                var payload = new byte[payloadLength];
                if (!TryReadExactly(stream, payload))
                {
                    return null;
                }

                // APP1 には XMP（"http://ns.adobe.com/..."）が入っていることもあるので、"Exif\0\0" のものだけ使う
                if (payload.AsSpan(0, ExifHeader.Length).SequenceEqual(ExifHeader))
                {
                    return payload[ExifHeader.Length..];
                }
                continue;
            }

            stream.Seek(payloadLength, SeekOrigin.Current);
        }

        return null;
    }

    /// <summary>PNG のチャンクを順に見て、eXIf を探す</summary>
    private static byte[]? FindPngExif(Stream stream)
    {
        stream.Position = 8;
        Span<byte> chunkHeader = stackalloc byte[8];

        for (int i = 0; i < MaxSegments; i++)
        {
            if (!TryReadExactly(stream, chunkHeader))
            {
                return null;
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
            var type = chunkHeader[4..8];

            if (length > stream.Length - stream.Position)
            {
                return null; // 長さがファイルからはみ出している
            }

            if (type.SequenceEqual("eXIf"u8))
            {
                return length <= MaxBlockBytes ? ReadBytes(stream, (int)length) : null;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return null;
            }

            stream.Seek(length + 4, SeekOrigin.Current); // データ + CRC
        }

        return null;
    }

    /// <summary>WebP（RIFF）のチャンクを順に見て、EXIF を探す</summary>
    private static byte[]? FindWebPExif(Stream stream)
    {
        stream.Position = 12;
        Span<byte> chunkHeader = stackalloc byte[8];

        for (int i = 0; i < MaxSegments; i++)
        {
            if (!TryReadExactly(stream, chunkHeader))
            {
                return null;
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            if (size > stream.Length - stream.Position)
            {
                return null;
            }

            if (chunkHeader[..4].SequenceEqual("EXIF"u8))
            {
                if (size > MaxBlockBytes)
                {
                    return null;
                }

                var block = ReadBytes(stream, (int)size);

                // 仕様では TIFF ヘッダから始まるが、JPEG と同じ "Exif\0\0" を付けて書くソフトもある
                return block is not null && block.AsSpan().StartsWith(ExifHeader) ? block[ExifHeader.Length..] : block;
            }

            stream.Seek(size + (size & 1), SeekOrigin.Current); // チャンクは偶数バイトに揃えてある
        }

        return null;
    }

    // ------------------------------------------------------------
    // TIFF 構造を読む
    // ------------------------------------------------------------

    /// <summary>stream の origin から length バイトが TIFF 構造。オフセットはすべて origin からの相対</summary>
    private static ExifData? ReadTiff(Stream stream, long origin, long length)
    {
        var source = new TiffSource(stream, origin, length);

        Span<byte> header = stackalloc byte[8];
        if (!source.TryRead(0, header))
        {
            return null;
        }

        if (header[0] == 'I' && header[1] == 'I')
        {
            source.BigEndian = false;
        }
        else if (header[0] == 'M' && header[1] == 'M')
        {
            source.BigEndian = true;
        }
        else
        {
            return null;
        }

        if (source.U16(header[2..4]) != 42)
        {
            return null;
        }

        var data = new ExifData();
        var visited = new HashSet<long>();
        var pointers = ReadIfd(source, source.U32(header[4..8]), ExifIfd.Primary, data, visited);

        // IFD0 から指されている Exif IFD と GPS IFD だけ読む（その先はたどらない）
        foreach (var (ifd, offset) in pointers)
        {
            ReadIfd(source, offset, ifd, data, visited);
        }

        return data;
    }

    /// <summary>IFD を1つ読んで data に入れる。IFD0 なら、Exif IFD / GPS IFD の位置を返す</summary>
    private static List<(ExifIfd Ifd, long Offset)> ReadIfd(
        TiffSource source, long offset, ExifIfd ifd, ExifData data, HashSet<long> visited)
    {
        var pointers = new List<(ExifIfd, long)>();

        // 同じ IFD を二度読まない（ポインタが自分や IFD0 を指している壊れたファイル対策）
        if (!visited.Add(offset))
        {
            return pointers;
        }

        Span<byte> countBytes = stackalloc byte[2];
        if (!source.TryRead(offset, countBytes))
        {
            return pointers;
        }

        int count = Math.Min((int)source.U16(countBytes), MaxEntriesPerIfd);
        Span<byte> raw = stackalloc byte[12];

        for (int i = 0; i < count; i++)
        {
            if (!source.TryRead(offset + 2 + 12L * i, raw))
            {
                break; // IFD の途中でデータが終わっている
            }

            ushort tag = source.U16(raw[0..2]);
            ushort type = source.U16(raw[2..4]);
            uint valueCount = source.U32(raw[4..8]);

            var entry = ReadEntry(source, tag, type, valueCount, raw[8..12]);
            if (entry is null)
            {
                continue; // 読まない型・壊れた値。このタグだけ飛ばす
            }

            data.Add(ifd, entry);

            if (ifd == ExifIfd.Primary && entry.Integers is [var pointer, ..])
            {
                if (tag == ExifTags.ExifIfdPointer)
                {
                    pointers.Add((ExifIfd.Exif, pointer));
                }
                else if (tag == ExifTags.GpsIfdPointer)
                {
                    pointers.Add((ExifIfd.Gps, pointer));
                }
            }
        }

        return pointers;
    }

    private static ExifEntry? ReadEntry(TiffSource source, ushort tag, ushort type, uint count, ReadOnlySpan<byte> valueOrOffset)
    {
        int size = TypeSize(type);
        if (size == 0 || count == 0 || (long)count * size > MaxValueBytes)
        {
            return null;
        }

        int total = (int)count * size;

        // 4バイト以下ならエントリの中にそのまま入っている。それより大きければ、そこに書かれた位置にある
        byte[] bytes = new byte[total];
        if (total <= 4)
        {
            valueOrOffset[..total].CopyTo(bytes);
        }
        else if (!source.TryRead(source.U32(valueOrOffset), bytes))
        {
            return null;
        }

        int n = (int)count;
        switch (type)
        {
            case 2: // ASCII（NUL まで。実際には UTF-8 や Shift_JIS で書くソフトもあるので、ExifText で読む）
            {
                int end = Array.IndexOf(bytes, (byte)0);
                var text = ExifText.Decode(bytes.AsSpan(0, end < 0 ? bytes.Length : end)).Trim();
                return new ExifEntry { Tag = tag, Type = type, Text = text };
            }

            case 1 or 7: // BYTE / UNDEFINED
            {
                var integers = new long[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                    integers[i] = bytes[i];

                return new ExifEntry { Tag = tag, Type = type, Bytes = bytes, Integers = integers };
            }

            case 6: // SBYTE
            {
                var integers = new long[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                    integers[i] = (sbyte)bytes[i];

                return new ExifEntry { Tag = tag, Type = type, Bytes = bytes, Integers = integers };
            }

            case 3 or 8: // SHORT / SSHORT
            {
                var values = new long[n];
                for (int i = 0; i < n; i++)
                {
                    ushort v = source.U16(bytes.AsSpan(i * 2, 2));
                    values[i] = type == 8 ? (short)v : v;
                }
                return new ExifEntry { Tag = tag, Type = type, Integers = values };
            }

            case 4 or 9: // LONG / SLONG
            {
                var values = new long[n];
                for (int i = 0; i < n; i++)
                {
                    uint v = source.U32(bytes.AsSpan(i * 4, 4));
                    values[i] = type == 9 ? (int)v : v;
                }
                return new ExifEntry { Tag = tag, Type = type, Integers = values };
            }

            case 5 or 10: // RATIONAL / SRATIONAL
            {
                var values = new Rational[n];
                for (int i = 0; i < n; i++)
                {
                    uint num = source.U32(bytes.AsSpan(i * 8, 4));
                    uint den = source.U32(bytes.AsSpan(i * 8 + 4, 4));
                    values[i] = type == 10 ? new Rational((int)num, (int)den) : new Rational(num, den);
                }
                return new ExifEntry { Tag = tag, Type = type, Rationals = values };
            }

            default:
                return null;
        }
    }

    /// <summary>型ごとの1個あたりのバイト数。読まない型（FLOAT / DOUBLE など）は 0</summary>
    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 => 4,
        5 or 10 => 8,
        _ => 0,
    };

    private static byte[]? ReadBytes(Stream stream, int length)
    {
        var buffer = new byte[length];
        return TryReadExactly(stream, buffer) ? buffer : null;
    }

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

    /// <summary>TIFF 構造の範囲だけを、オフセット指定で読む。範囲外は読まずに false</summary>
    private sealed class TiffSource(Stream stream, long origin, long length)
    {
        public bool BigEndian { get; set; }

        public bool TryRead(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset > length - buffer.Length)
            {
                return false;
            }

            stream.Position = origin + offset;
            return TryReadExactly(stream, buffer);
        }

        public ushort U16(ReadOnlySpan<byte> b) =>
            BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

        public uint U32(ReadOnlySpan<byte> b) =>
            BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
    }
}
