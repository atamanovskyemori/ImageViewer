using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ImageViewer.Metadata;

/// <summary>HEIF（HEIC / AVIF）から読んだ情報。読めなかったものは null</summary>
/// <param name="Exif">EXIF の TIFF 構造（"II*\0" / "MM\0*" から始まるバイト列）</param>
/// <param name="Width">主画像の幅（回転前。ispe）</param>
/// <param name="Height">主画像の高さ（回転前。ispe）</param>
/// <param name="Rotation">表示するときの回転（反時計回りの角度。irot。0 / 90 / 180 / 270）</param>
/// <param name="Brand">ftyp の主ブランド（"heic" / "avif" など）</param>
public sealed record HeifInfo(byte[]? Exif, int? Width, int? Height, int Rotation, string Brand);

/// <summary>
/// HEIF（HEIC / AVIF）のメタデータを読む（書き込みはしない）。画像はデコードしない。
/// HEIF は ISOBMFF（MP4 と同じ「箱」の入れ子）で、EXIF は meta 箱の中で「Exif」という種類のアイテムとして入っている。
/// <list type="number">
/// <item>iinf で、種類が Exif のアイテムの番号を探す</item>
/// <item>iloc で、そのアイテムがファイル（または idat 箱）のどこにあるかを調べて読む</item>
/// <item>中身は「TIFF ヘッダまでの距離（4バイト）」の後に TIFF 構造が続く</item>
/// </list>
/// 主画像の大きさ（ispe）と回転（irot）は、pitm の主画像に iprp / ipma でひも付いたものを読む。
/// 壊れたデータでも例外は投げず、読めた分だけ返す。
/// </summary>
public static class HeifReader
{
    // 壊れたファイルで延々と読まないための上限
    private const int MaxMetaBytes = 16 * 1024 * 1024;
    private const int MaxExifBytes = 16 * 1024 * 1024;
    private const int MaxBoxes = 10000;

    // HEIF として扱う ftyp の主ブランド（iPhone の写真は heic、AVIF は avif）
    private static readonly HashSet<string> Brands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "avif", "avis",
    };

    /// <summary>ファイル先頭（12バイト以上）が HEIF の ftyp か</summary>
    public static bool IsHeif(ReadOnlySpan<byte> header) => BrandOf(header) is not null;

    /// <summary>ファイル先頭が HEIF の ftyp なら、主ブランドを返す</summary>
    public static string? BrandOf(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || !header[4..8].SequenceEqual("ftyp"u8))
        {
            return null;
        }
        var brand = Encoding.ASCII.GetString(header[8..12]);
        return Brands.Contains(brand) ? brand : null;
    }

    /// <summary>HEIF を読む。HEIF でない・meta 箱が無いなら null。stream はシークできること</summary>
    public static HeifInfo? Read(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            stream.Position = 0;
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
                || BrandOf(header) is not { } brand)
            {
                return null;
            }

            if (FindTopLevelMeta(stream) is not { } meta)
            {
                return null;
            }

            return ReadMeta(stream, meta, brand);
        }
        catch (IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------
    // 箱を読む
    // ------------------------------------------------------------

    /// <summary>ファイルの一番外側の箱を順に見て、meta 箱の中身（バージョンとフラグの4バイトは除く）を読む</summary>
    private static byte[]? FindTopLevelMeta(Stream stream)
    {
        long position = 0;
        Span<byte> boxHeader = stackalloc byte[16];

        for (int i = 0; i < MaxBoxes && position + 8 <= stream.Length; i++)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(boxHeader[..8], 8, throwOnEndOfStream: false) < 8)
            {
                return null;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(boxHeader);
            int headerSize = 8;
            if (size == 1)
            {
                // 64 ビットの大きさ
                if (stream.ReadAtLeast(boxHeader[8..16], 8, throwOnEndOfStream: false) < 8)
                {
                    return null;
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
                return null; // 壊れている
            }

            if (boxHeader[4..8].SequenceEqual("meta"u8))
            {
                long bodySize = size - headerSize - 4; // meta はフル箱（バージョン + フラグ 4バイト）
                if (bodySize < 0 || bodySize > MaxMetaBytes)
                {
                    return null;
                }

                stream.Position = position + headerSize + 4;
                var body = new byte[bodySize];
                return stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) == body.Length ? body : null;
            }

            position += size;
        }
        return null;
    }

    /// <summary>data の中の箱を順に返す（種類と、中身の範囲）。壊れていたら、そこで終わる</summary>
    private static IEnumerable<(string Type, int Start, int Length)> Boxes(byte[] data, int start, int end)
    {
        int position = start;
        for (int i = 0; i < MaxBoxes && position + 8 <= end; i++)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            int headerSize = 8;
            if (size == 1)
            {
                if (position + 16 > end)
                {
                    yield break;
                }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < headerSize || size > end - position)
            {
                yield break;
            }

            yield return (Encoding.ASCII.GetString(data, position + 4, 4), position + headerSize, (int)size - headerSize);
            position += (int)size;
        }
    }

    private static HeifInfo ReadMeta(Stream stream, byte[] meta, string brand)
    {
        uint? primary = null;
        uint? exifItem = null;
        (int Start, int Length)? iloc = null, idat = null, iprp = null;

        foreach (var (type, start, length) in Boxes(meta, 0, meta.Length))
        {
            switch (type)
            {
                case "pitm":
                    primary = ReadPrimaryItem(meta, start, length);
                    break;
                case "iinf":
                    exifItem = FindExifItem(meta, start, length);
                    break;
                case "iloc":
                    iloc = (start, length);
                    break;
                case "idat":
                    idat = (start, length);
                    break;
                case "iprp":
                    iprp = (start, length);
                    break;
            }
        }

        byte[]? exif = null;
        if (exifItem is { } item && iloc is { } ilocBox)
        {
            var payload = ReadItem(stream, meta, ilocBox.Start, ilocBox.Length, item, idat);
            exif = payload is null ? null : TiffFromExifItem(payload);
        }

        int? width = null, height = null;
        int rotation = 0;
        if (primary is { } primaryItem && iprp is { } iprpBox)
        {
            (width, height, rotation) = ReadPrimaryProperties(meta, iprpBox.Start, iprpBox.Length, primaryItem);
        }

        return new HeifInfo(exif, width, height, rotation, brand);
    }

    /// <summary>pitm：主画像のアイテム番号</summary>
    private static uint? ReadPrimaryItem(byte[] meta, int start, int length)
    {
        if (length < 6)
        {
            return null;
        }
        int version = meta[start];
        return version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(start + 4))
            : length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(start + 4)) : null;
    }

    /// <summary>iinf：アイテムの一覧（infe 箱）から、種類が "Exif" のアイテムの番号を探す</summary>
    private static uint? FindExifItem(byte[] meta, int start, int length)
    {
        if (length < 6)
        {
            return null;
        }
        int version = meta[start];
        int entriesStart = start + 4 + (version == 0 ? 2 : 4); // バージョン・フラグ + 個数

        foreach (var (type, infeStart, infeLength) in Boxes(meta, entriesStart, start + length))
        {
            if (type != "infe" || infeLength < 4)
            {
                continue;
            }

            // バージョン 2 / 3 だけがアイテムの種類（4文字）を持つ
            int infeVersion = meta[infeStart];
            int idSize = infeVersion switch { 2 => 2, 3 => 4, _ => 0 };
            if (idSize == 0 || infeLength < 4 + idSize + 2 + 4)
            {
                continue;
            }

            int p = infeStart + 4;
            uint id = idSize == 2 ? BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(p));
            p += idSize + 2; // アイテム番号 + 保護の番号
            if (meta.AsSpan(p, 4).SequenceEqual("Exif"u8))
            {
                return id;
            }
        }
        return null;
    }

    /// <summary>
    /// iloc：アイテムの置き場所を調べて、中身を読む（いくつかに分かれていたらつなげる）。
    /// 置き場所はファイルの位置（construction_method 0）か、idat 箱の中の位置（1）
    /// </summary>
    private static byte[]? ReadItem(Stream stream, byte[] meta, int start, int length, uint wanted, (int Start, int Length)? idat)
    {
        int end = start + length;
        int p = start;
        if (end - p < 8)
        {
            return null;
        }

        int version = meta[p];
        p += 4; // バージョン・フラグ
        int offsetSize = meta[p] >> 4;
        int lengthSize = meta[p] & 0x0F;
        int baseOffsetSize = meta[p + 1] >> 4;
        int indexSize = version is 1 or 2 ? meta[p + 1] & 0x0F : 0;
        p += 2;

        if (!TryReadUInt(meta, ref p, end, version < 2 ? 2 : 4, out ulong itemCount))
        {
            return null;
        }

        for (ulong i = 0; i < itemCount && i < MaxBoxes; i++)
        {
            if (!TryReadUInt(meta, ref p, end, version < 2 ? 2 : 4, out ulong itemId))
            {
                return null;
            }

            int constructionMethod = 0;
            if (version is 1 or 2)
            {
                if (!TryReadUInt(meta, ref p, end, 2, out ulong method))
                {
                    return null;
                }
                constructionMethod = (int)(method & 0x0F);
            }

            if (!TryReadUInt(meta, ref p, end, 2, out _) // データの参照先（このファイルなら 0）
                || !TryReadUInt(meta, ref p, end, baseOffsetSize, out ulong baseOffset)
                || !TryReadUInt(meta, ref p, end, 2, out ulong extentCount))
            {
                return null;
            }

            var extents = new List<(ulong Offset, ulong Length)>();
            for (ulong e = 0; e < extentCount && e < MaxBoxes; e++)
            {
                if (!TryReadUInt(meta, ref p, end, indexSize, out _)
                    || !TryReadUInt(meta, ref p, end, offsetSize, out ulong extentOffset)
                    || !TryReadUInt(meta, ref p, end, lengthSize, out ulong extentLength))
                {
                    return null;
                }
                extents.Add((baseOffset + extentOffset, extentLength));
            }

            if (itemId != wanted)
            {
                continue;
            }

            return constructionMethod switch
            {
                0 => ReadFromFile(stream, extents),
                1 when idat is { } box => ReadFromIdat(meta, box, extents),
                _ => null, // 別のアイテムから組み立てるもの（2）は扱わない
            };
        }
        return null;
    }

    private static byte[]? ReadFromFile(Stream stream, List<(ulong Offset, ulong Length)> extents)
    {
        ulong total = extents.Aggregate(0UL, (sum, e) => sum + e.Length);
        if (total == 0 || total > MaxExifBytes)
        {
            return null;
        }

        var data = new byte[total];
        int written = 0;
        foreach (var (offset, length) in extents)
        {
            if (offset + length > (ulong)stream.Length)
            {
                return null;
            }
            stream.Position = (long)offset;
            if (stream.ReadAtLeast(data.AsSpan(written, (int)length), (int)length, throwOnEndOfStream: false) < (int)length)
            {
                return null;
            }
            written += (int)length;
        }
        return data;
    }

    private static byte[]? ReadFromIdat(byte[] meta, (int Start, int Length) idat, List<(ulong Offset, ulong Length)> extents)
    {
        var data = new List<byte>();
        foreach (var (offset, length) in extents)
        {
            if (offset + length > (ulong)idat.Length || (ulong)data.Count + length > MaxExifBytes)
            {
                return null;
            }
            data.AddRange(meta.AsSpan(idat.Start + (int)offset, (int)length).ToArray());
        }
        return data.Count > 0 ? data.ToArray() : null;
    }

    /// <summary>
    /// Exif アイテムの中身から TIFF 構造を取り出す。先頭4バイトが「その後ろから TIFF ヘッダまでの距離」
    /// （ふつうは "Exif\0\0" の分の 6）。距離がおかしければ、TIFF ヘッダを探す
    /// </summary>
    internal static byte[]? TiffFromExifItem(byte[] payload)
    {
        if (payload.Length >= 4)
        {
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(payload);
            long start = 4L + offset;
            if (start + 8 <= payload.Length && IsTiffHeader(payload.AsSpan((int)start)))
            {
                return payload[(int)start..];
            }
        }

        for (int i = 0; i + 8 <= payload.Length && i < 64; i++)
        {
            if (IsTiffHeader(payload.AsSpan(i)))
            {
                return payload[i..];
            }
        }
        return null;
    }

    private static bool IsTiffHeader(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && (data[..4].SequenceEqual("II*\0"u8) || data[..4].SequenceEqual("MM\0*"u8));

    /// <summary>
    /// iprp：ipco（プロパティの一覧）と ipma（アイテムとプロパティのひも付け）から、
    /// 主画像の大きさ（ispe）と回転（irot）を読む
    /// </summary>
    private static (int? Width, int? Height, int Rotation) ReadPrimaryProperties(byte[] meta, int start, int length, uint primary)
    {
        var properties = new List<(string Type, int Start, int Length)>();
        var associated = new List<int>();

        foreach (var (type, boxStart, boxLength) in Boxes(meta, start, start + length))
        {
            if (type == "ipco")
            {
                properties.AddRange(Boxes(meta, boxStart, boxStart + boxLength));
            }
            else if (type == "ipma")
            {
                associated.AddRange(ReadAssociations(meta, boxStart, boxLength, primary));
            }
        }

        int? width = null, height = null;
        int rotation = 0;
        foreach (int index in associated)
        {
            if (index < 1 || index > properties.Count)
            {
                continue; // 番号は 1 から
            }

            var (type, propStart, propLength) = properties[index - 1];
            if (type == "ispe" && propLength >= 12 && width is null)
            {
                width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(propStart + 4)));
                height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(propStart + 8)));
            }
            else if (type == "irot" && propLength >= 1)
            {
                rotation = (meta[propStart] & 0x03) * 90;
            }
        }
        return (width > 0 ? width : null, height > 0 ? height : null, rotation);
    }

    /// <summary>ipma：item の番号にひも付いたプロパティの番号（1 から）</summary>
    private static IEnumerable<int> ReadAssociations(byte[] meta, int start, int length, uint item)
    {
        int end = start + length;
        int p = start;
        if (end - p < 8)
        {
            yield break;
        }

        int version = meta[p];
        bool largeIndex = (meta[p + 3] & 0x01) != 0; // フラグの最下位ビット：番号が 15 ビット
        p += 4;
        if (!TryReadUInt(meta, ref p, end, 4, out ulong entryCount))
        {
            yield break;
        }

        for (ulong i = 0; i < entryCount && i < MaxBoxes; i++)
        {
            if (!TryReadUInt(meta, ref p, end, version < 1 ? 2 : 4, out ulong id)
                || !TryReadUInt(meta, ref p, end, 1, out ulong count))
            {
                yield break;
            }

            for (ulong j = 0; j < count; j++)
            {
                if (!TryReadUInt(meta, ref p, end, largeIndex ? 2 : 1, out ulong value))
                {
                    yield break;
                }
                if (id == item)
                {
                    yield return (int)(value & (largeIndex ? 0x7FFFUL : 0x7FUL)); // 最上位ビットは「必須か」の印
                }
            }
        }
    }

    /// <summary>size バイト（0 / 1 / 2 / 4 / 8）のビッグエンディアンの整数を読んで進める。足りなければ false</summary>
    private static bool TryReadUInt(byte[] data, ref int position, int end, int size, out ulong value)
    {
        value = 0;
        if (size is not (0 or 1 or 2 or 4 or 8) || position + size > end)
        {
            return false;
        }
        for (int i = 0; i < size; i++)
        {
            value = (value << 8) | data[position + i];
        }
        position += size;
        return true;
    }
}
