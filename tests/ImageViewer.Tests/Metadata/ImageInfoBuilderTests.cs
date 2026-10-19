using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class ImageInfoBuilderTests
    {
        private static FileFacts Facts(string format = "JPEG", bool appliesOrientation = true) => new(
            @"C:\Pics\IMG_0001.JPG",
            3_354_112,
            new DateTime(2024, 5, 2, 8, 0, 0),
            format,
            4000,
            3000,
            appliesOrientation);

        private static string? Value(ImageInfo info, string section, string label) =>
            info.Sections.FirstOrDefault(s => s.Title == section)?.Rows.FirstOrDefault(r => r.Label == label)?.Value;

        [TestMethod]
        public void Without_Exif_Only_File_Section_Is_Shown()
        {
            var info = ImageInfoBuilder.Build(Facts(), null);

            Assert.AreEqual(1, info.Sections.Count);
            Assert.AreEqual("ファイル", info.Sections[0].Title);
            Assert.AreEqual("IMG_0001.JPG", Value(info, "ファイル", "ファイル名"));
            Assert.AreEqual("4000 × 3000", Value(info, "ファイル", "ピクセル数"));
            Assert.AreEqual("3.2 MB（3,354,112 バイト）", Value(info, "ファイル", "ファイルサイズ"));
            Assert.AreEqual("2024/05/02 08:00:00", Value(info, "ファイル", "更新日時"));
        }

        [TestMethod]
        public void Sections_Without_Values_Are_Hidden()
        {
            var exif = ExifReader.ReadTiff(new TiffBuilder()
                .Ascii(ExifIfd.Primary, ExifTags.Make, "Canon")
                .Build());

            var info = ImageInfoBuilder.Build(Facts(), exif);

            CollectionAssert.AreEqual(new[] { "ファイル", "撮影" }, info.Sections.Select(s => s.Title).ToArray());
            Assert.AreEqual(1, info.Sections[1].Rows.Count, "Only rows with values");
        }

        [TestMethod]
        public void Pixel_Size_Follows_Orientation_Only_When_The_Viewer_Rotates()
        {
            var exif = ExifReader.ReadTiff(new TiffBuilder()
                .Short(ExifIfd.Primary, ExifTags.Orientation, 6)
                .Build());

            var jpeg = ImageInfoBuilder.Build(Facts("JPEG", appliesOrientation: true), exif);
            var webp = ImageInfoBuilder.Build(Facts("WebP", appliesOrientation: false), exif);

            Assert.AreEqual("3000 × 4000", Value(jpeg, "ファイル", "ピクセル数"));
            Assert.AreEqual("4000 × 3000", Value(webp, "ファイル", "ピクセル数"));
            Assert.AreEqual("右90°回転", Value(jpeg, "その他", "向き"));
        }

        [TestMethod]
        public void Shooting_And_Gps_Rows()
        {
            var exif = ExifReader.ReadTiff(new TiffBuilder()
                .Ascii(ExifIfd.Primary, ExifTags.DateTime, "2024:05:02 08:00:00")
                .Ascii(ExifIfd.Exif, ExifTags.DateTimeOriginal, "2024:05:01 12:34:56")
                .Ascii(ExifIfd.Exif, ExifTags.OffsetTimeOriginal, "+09:00")
                .Ascii(ExifIfd.Exif, ExifTags.LensModel, "RF24-105mm F4 L IS USM")
                .Rational(ExifIfd.Exif, ExifTags.FocalLength, (50, 1))
                .Short(ExifIfd.Exif, ExifTags.FocalLengthIn35mm, 50)
                .Rational(ExifIfd.Exif, ExifTags.ExposureTime, (1, 250))
                .Short(ExifIfd.Exif, ExifTags.Flash, 0x10)
                .Ascii(ExifIfd.Gps, ExifTags.GpsLatitudeRef, "N")
                .Rational(ExifIfd.Gps, ExifTags.GpsLatitude, (35, 1), (39, 1), (312, 10))
                .Ascii(ExifIfd.Gps, ExifTags.GpsLongitudeRef, "E")
                .Rational(ExifIfd.Gps, ExifTags.GpsLongitude, (139, 1), (44, 1), (0, 1))
                .Byte(ExifIfd.Gps, ExifTags.GpsAltitudeRef, 0)
                .Rational(ExifIfd.Gps, ExifTags.GpsAltitude, (401, 10))
                .Build());

            var info = ImageInfoBuilder.Build(Facts(), exif);

            Assert.AreEqual("2024/05/01 12:34:56（+09:00）", Value(info, "撮影", "撮影日時"), "DateTimeOriginal wins over DateTime");
            Assert.AreEqual("RF24-105mm F4 L IS USM", Value(info, "撮影", "レンズ"));
            Assert.AreEqual("50 mm（35mm換算 50 mm）", Value(info, "撮影", "焦点距離"));
            Assert.AreEqual("1/250 秒", Value(info, "撮影", "シャッター速度"));
            Assert.AreEqual("発光なし", Value(info, "撮影", "フラッシュ"));
            Assert.AreEqual("35°39'31.2\"N（35.658667）", Value(info, "位置情報", "緯度"));
            Assert.AreEqual("139°44'00.0\"E（139.733333）", Value(info, "位置情報", "経度"));
            Assert.AreEqual("40.1 m", Value(info, "位置情報", "高度"));
        }

        [TestMethod]
        public void Falls_Back_To_DateTime_When_Original_Is_Missing()
        {
            var exif = ExifReader.ReadTiff(new TiffBuilder()
                .Ascii(ExifIfd.Primary, ExifTags.DateTime, "2024:05:02 08:00:00")
                .Build());

            Assert.AreEqual("2024/05/02 08:00:00", Value(ImageInfoBuilder.Build(Facts(), exif), "撮影", "撮影日時"));
        }

        [TestMethod]
        public void ToText_Lists_Sections_And_Rows()
        {
            var info = new ImageInfo("x", [
                new InfoSection("ファイル", [new InfoRow("ファイル名", "a.jpg")]),
                new InfoSection("撮影", [new InfoRow("F値", "F2.8"), new InfoRow("ISO感度", "ISO 100")]),
            ]);

            var expected = string.Join(Environment.NewLine,
                "[ファイル]", "ファイル名: a.jpg", "", "[撮影]", "F値: F2.8", "ISO感度: ISO 100", "");

            Assert.AreEqual(expected, info.ToText());
        }
    }
}
