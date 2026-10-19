using System.Globalization;
using System.Text;

namespace ImageViewer.Metadata;

/// <summary>情報パネルの1行（項目名と値）</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>情報パネルの見出し1つぶん（「ファイル」「撮影」など）</summary>
public sealed record InfoSection(string Title, IReadOnlyList<InfoRow> Rows);

/// <summary>1枚の画像の情報。情報パネルにそのまま出す</summary>
public sealed record ImageInfo(string Path, IReadOnlyList<InfoSection> Sections)
{
    /// <summary>コピー用のテキスト（「項目名: 値」を1行ずつ、見出しごとに空行で区切る）</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var section in Sections)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }
            text.Append('[').Append(section.Title).AppendLine("]");
            foreach (var row in section.Rows)
            {
                text.Append(row.Label).Append(": ").AppendLine(row.Value);
            }
        }
        return text.ToString();
    }
}

/// <summary>EXIF 以外の、ファイルそのものの情報</summary>
/// <param name="Format">形式の表示名（「JPEG」「WebP（アニメーション）」など）</param>
/// <param name="Width">縦横のピクセル数。読めなかったら null</param>
/// <param name="AppliesExifOrientation">表示で EXIF の向きを反映する形式か（JPEG / TIFF）。反映するなら縦横を入れ替えて出す</param>
public sealed record FileFacts(
    string Path,
    long? Length,
    DateTime? LastWriteTime,
    string? Format,
    int? Width,
    int? Height,
    bool AppliesExifOrientation);

/// <summary>ファイルの情報と EXIF から、情報パネルに出す行を作る。値が無い・おかしい行は出さない</summary>
public static class ImageInfoBuilder
{
    public static ImageInfo Build(FileFacts file, ExifData? exif)
    {
        var sections = new List<InfoSection>();

        // ----- ファイル -----
        var fileRows = new List<InfoRow>();
        Add(fileRows, "ファイル名", System.IO.Path.GetFileName(file.Path));
        Add(fileRows, "形式", file.Format);
        Add(fileRows, "ピクセル数", PixelSize(file, exif));
        Add(fileRows, "ファイルサイズ", file.Length is { } length ? ExifFormat.FileSize(length) : null);
        Add(fileRows, "更新日時", file.LastWriteTime?.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture));
        AddSection(sections, "ファイル", fileRows);

        if (exif is null)
        {
            return new ImageInfo(file.Path, sections);
        }

        // ----- 撮影 -----
        var shotRows = new List<InfoRow>();
        Add(shotRows, "撮影日時", ExifFormat.DateTime(
            exif.GetText(ExifIfd.Exif, ExifTags.DateTimeOriginal) ?? exif.GetText(ExifIfd.Primary, ExifTags.DateTime),
            exif.GetText(ExifIfd.Exif, ExifTags.OffsetTimeOriginal)));
        Add(shotRows, "メーカー", exif.GetText(ExifIfd.Primary, ExifTags.Make));
        Add(shotRows, "機種", exif.GetText(ExifIfd.Primary, ExifTags.Model));
        Add(shotRows, "レンズ", exif.GetText(ExifIfd.Exif, ExifTags.LensModel) ?? exif.GetText(ExifIfd.Exif, ExifTags.LensMake));
        Add(shotRows, "焦点距離", ExifFormat.FocalLength(
            exif.GetRational(ExifIfd.Exif, ExifTags.FocalLength),
            exif.GetInteger(ExifIfd.Exif, ExifTags.FocalLengthIn35mm)));
        Add(shotRows, "F値", ExifFormat.FNumber(exif.GetRational(ExifIfd.Exif, ExifTags.FNumber)));
        Add(shotRows, "シャッター速度", ExifFormat.ExposureTime(exif.GetRational(ExifIfd.Exif, ExifTags.ExposureTime)));
        Add(shotRows, "ISO感度", ExifFormat.Iso(exif.GetInteger(ExifIfd.Exif, ExifTags.IsoSpeed)));
        Add(shotRows, "露出補正", ExifFormat.ExposureBias(exif.GetRational(ExifIfd.Exif, ExifTags.ExposureBias)));
        Add(shotRows, "露出モード", ExifFormat.ExposureProgram(exif.GetInteger(ExifIfd.Exif, ExifTags.ExposureProgram)));
        Add(shotRows, "測光方式", ExifFormat.MeteringMode(exif.GetInteger(ExifIfd.Exif, ExifTags.MeteringMode)));
        Add(shotRows, "フラッシュ", ExifFormat.Flash(exif.GetInteger(ExifIfd.Exif, ExifTags.Flash)));
        Add(shotRows, "ホワイトバランス", ExifFormat.WhiteBalance(exif.GetInteger(ExifIfd.Exif, ExifTags.WhiteBalance)));
        AddSection(sections, "撮影", shotRows);

        // ----- 位置情報 -----
        var gpsRows = new List<InfoRow>();
        Add(gpsRows, "緯度", ExifFormat.GpsCoordinate(
            exif.GetRationals(ExifIfd.Gps, ExifTags.GpsLatitude),
            exif.GetText(ExifIfd.Gps, ExifTags.GpsLatitudeRef)));
        Add(gpsRows, "経度", ExifFormat.GpsCoordinate(
            exif.GetRationals(ExifIfd.Gps, ExifTags.GpsLongitude),
            exif.GetText(ExifIfd.Gps, ExifTags.GpsLongitudeRef)));
        Add(gpsRows, "高度", ExifFormat.Altitude(
            exif.GetRational(ExifIfd.Gps, ExifTags.GpsAltitude),
            exif.GetInteger(ExifIfd.Gps, ExifTags.GpsAltitudeRef)));
        AddSection(sections, "位置情報", gpsRows);

        // ----- その他 -----
        var otherRows = new List<InfoRow>();
        Add(otherRows, "向き", ExifFormat.Orientation(exif.GetInteger(ExifIfd.Primary, ExifTags.Orientation)));
        Add(otherRows, "ソフトウェア", exif.GetText(ExifIfd.Primary, ExifTags.Software));
        Add(otherRows, "作者", exif.GetText(ExifIfd.Primary, ExifTags.Artist));
        Add(otherRows, "著作権", exif.GetText(ExifIfd.Primary, ExifTags.Copyright));
        AddSection(sections, "その他", otherRows);

        return new ImageInfo(file.Path, sections);
    }

    /// <summary>
    /// 「4000 × 3000」。JPEG / TIFF で EXIF の向きが 90° 回転（5〜8）なら、表示に合わせて縦横を入れ替える
    /// （ステータスバーの縦横と揃えるため）
    /// </summary>
    private static string? PixelSize(FileFacts file, ExifData? exif)
    {
        if (file.Width is not > 0 || file.Height is not > 0)
        {
            return null;
        }

        int width = file.Width.Value;
        int height = file.Height.Value;
        if (file.AppliesExifOrientation && exif?.GetInteger(ExifIfd.Primary, ExifTags.Orientation) is >= 5 and <= 8)
        {
            (width, height) = (height, width);
        }
        return $"{width} × {height}";
    }

    private static void Add(List<InfoRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new InfoRow(label, value));
        }
    }

    private static void AddSection(List<InfoSection> sections, string title, List<InfoRow> rows)
    {
        if (rows.Count > 0)
        {
            sections.Add(new InfoSection(title, rows));
        }
    }
}
