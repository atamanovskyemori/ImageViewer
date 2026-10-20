using System.Globalization;

namespace ImageViewer.Metadata;

/// <summary>EXIF のタグが入っている IFD（タグの置き場所）。同じ番号でも IFD が違えば別のタグ</summary>
public enum ExifIfd
{
    /// <summary>IFD0（メーカー・機種・向きなど）</summary>
    Primary,

    /// <summary>Exif IFD（撮影条件）</summary>
    Exif,

    /// <summary>GPS IFD（位置情報）</summary>
    Gps,
}

/// <summary>EXIF の有理数（分子 / 分母）。分母が 0 のものは壊れた値として扱う</summary>
public readonly record struct Rational(long Numerator, long Denominator)
{
    public bool IsValid => Denominator != 0;

    public double ToDouble() => (double)Numerator / Denominator;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
}

/// <summary>
/// EXIF のタグ1つ。型に応じて Text / Integers / Rationals のどれかが入る。
/// BYTE / UNDEFINED はバイト列（Bytes）でも持つ。
/// </summary>
public sealed class ExifEntry
{
    public required ushort Tag { get; init; }

    /// <summary>TIFF の型番号（1: BYTE, 2: ASCII, 3: SHORT, 4: LONG, 5: RATIONAL, 7: UNDEFINED, 8: SSHORT, 9: SLONG, 10: SRATIONAL など）</summary>
    public required ushort Type { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<long>? Integers { get; init; }

    public IReadOnlyList<Rational>? Rationals { get; init; }

    public byte[]? Bytes { get; init; }
}

/// <summary>読み取った EXIF。必要な値は Get 系のメソッドで、型を気にせず取り出す</summary>
public sealed class ExifData
{
    private readonly Dictionary<(ExifIfd, ushort), ExifEntry> _entries = new();

    public int Count => _entries.Count;

    /// <summary>同じ IFD に同じタグが2つあったら、先に読んだ方を使う</summary>
    internal void Add(ExifIfd ifd, ExifEntry entry) => _entries.TryAdd((ifd, entry.Tag), entry);

    public ExifEntry? Get(ExifIfd ifd, ushort tag) =>
        _entries.TryGetValue((ifd, tag), out var entry) ? entry : null;

    /// <summary>文字列のタグ。空なら null</summary>
    public string? GetText(ExifIfd ifd, ushort tag) =>
        Get(ifd, tag)?.Text is { Length: > 0 } text ? text : null;

    /// <summary>整数のタグ（BYTE / SHORT / LONG など）の index 番目</summary>
    public long? GetInteger(ExifIfd ifd, ushort tag, int index = 0) =>
        Get(ifd, tag)?.Integers is { } values && index < values.Count ? values[index] : null;

    /// <summary>有理数のタグの index 番目。分母が 0 なら null</summary>
    public Rational? GetRational(ExifIfd ifd, ushort tag, int index = 0) =>
        Get(ifd, tag)?.Rationals is { } values && index < values.Count && values[index].IsValid ? values[index] : null;

    public IReadOnlyList<Rational>? GetRationals(ExifIfd ifd, ushort tag) => Get(ifd, tag)?.Rationals;
}

/// <summary>使うタグの番号（EXIF 2.3 / TIFF 6.0）</summary>
public static class ExifTags
{
    // IFD0
    public const ushort Make = 0x010F;
    public const ushort Model = 0x0110;
    public const ushort Orientation = 0x0112;
    public const ushort Software = 0x0131;
    public const ushort DateTime = 0x0132;
    public const ushort Artist = 0x013B;
    public const ushort Copyright = 0x8298;
    public const ushort ExifIfdPointer = 0x8769;
    public const ushort GpsIfdPointer = 0x8825;

    // Exif IFD
    public const ushort ExposureTime = 0x829A;
    public const ushort FNumber = 0x829D;
    public const ushort ExposureProgram = 0x8822;
    public const ushort IsoSpeed = 0x8827;
    public const ushort DateTimeOriginal = 0x9003;
    public const ushort OffsetTimeOriginal = 0x9011;
    public const ushort ExposureBias = 0x9204;
    public const ushort MeteringMode = 0x9207;
    public const ushort Flash = 0x9209;
    public const ushort FocalLength = 0x920A;
    public const ushort WhiteBalance = 0xA403;
    public const ushort FocalLengthIn35mm = 0xA405;
    public const ushort LensMake = 0xA433;
    public const ushort LensModel = 0xA434;
    public const ushort UserComment = 0x9286;

    // GPS IFD
    public const ushort GpsLatitudeRef = 0x0001;
    public const ushort GpsLatitude = 0x0002;
    public const ushort GpsLongitudeRef = 0x0003;
    public const ushort GpsLongitude = 0x0004;
    public const ushort GpsAltitudeRef = 0x0005;
    public const ushort GpsAltitude = 0x0006;
}
