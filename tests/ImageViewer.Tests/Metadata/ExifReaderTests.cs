using System.Buffers.Binary;
using System.IO;
using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class ExifReaderTests
    {
        private static TiffBuilder Sample(bool bigEndian = false) => new TiffBuilder(bigEndian)
            .Ascii(ExifIfd.Primary, ExifTags.Make, "Canon")
            .Ascii(ExifIfd.Primary, ExifTags.Model, "Canon EOS R5")
            .Short(ExifIfd.Primary, ExifTags.Orientation, 6)
            .Rational(ExifIfd.Exif, ExifTags.ExposureTime, (1, 250))
            .Rational(ExifIfd.Exif, ExifTags.FNumber, (28, 10))
            .Short(ExifIfd.Exif, ExifTags.IsoSpeed, 400)
            .Ascii(ExifIfd.Exif, ExifTags.DateTimeOriginal, "2024:05:01 12:34:56")
            .SRational(ExifIfd.Exif, ExifTags.ExposureBias, -2, 3)
            .Ascii(ExifIfd.Gps, ExifTags.GpsLatitudeRef, "N")
            .Rational(ExifIfd.Gps, ExifTags.GpsLatitude, (35, 1), (39, 1), (312, 10));

        private static ExifData? Read(byte[] file)
        {
            using var stream = new MemoryStream(file, writable: false);
            return ExifReader.Read(stream);
        }

        private static void AssertSample(ExifData? exif)
        {
            Assert.IsNotNull(exif);
            Assert.AreEqual("Canon", exif.GetText(ExifIfd.Primary, ExifTags.Make));
            Assert.AreEqual("Canon EOS R5", exif.GetText(ExifIfd.Primary, ExifTags.Model));
            Assert.AreEqual(6L, exif.GetInteger(ExifIfd.Primary, ExifTags.Orientation));
            Assert.AreEqual(new Rational(1, 250), exif.GetRational(ExifIfd.Exif, ExifTags.ExposureTime));
            Assert.AreEqual(new Rational(28, 10), exif.GetRational(ExifIfd.Exif, ExifTags.FNumber));
            Assert.AreEqual(400L, exif.GetInteger(ExifIfd.Exif, ExifTags.IsoSpeed));
            Assert.AreEqual("2024:05:01 12:34:56", exif.GetText(ExifIfd.Exif, ExifTags.DateTimeOriginal));
            Assert.AreEqual(new Rational(-2, 3), exif.GetRational(ExifIfd.Exif, ExifTags.ExposureBias));
            Assert.AreEqual("N", exif.GetText(ExifIfd.Gps, ExifTags.GpsLatitudeRef));

            var latitude = exif.GetRationals(ExifIfd.Gps, ExifTags.GpsLatitude);
            Assert.IsNotNull(latitude);
            CollectionAssert.AreEqual(
                new[] { new Rational(35, 1), new Rational(39, 1), new Rational(312, 10) },
                latitude.ToArray());
        }

        [TestMethod]
        public void Reads_Little_Endian_Tiff() => AssertSample(Read(Sample().Build()));

        [TestMethod]
        public void Reads_Big_Endian_Tiff() => AssertSample(Read(Sample(bigEndian: true).Build()));

        [TestMethod]
        public void Reads_Tiff_Block_Directly() => AssertSample(ExifReader.ReadTiff(Sample().Build()));

        [TestMethod]
        public void Reads_Jpeg_App1_And_Skips_Xmp() => AssertSample(Read(TiffBuilder.WrapJpeg(Sample().Build())));

        [TestMethod]
        public void Jpeg_Without_Exif_Returns_Null()
        {
            Assert.IsNull(Read(TiffBuilder.WrapJpeg(Sample().Build(), includeExif: false)));
        }

        [TestMethod]
        public void Jpeg_Stops_At_Image_Data()
        {
            // Anything after SOS is image data, not metadata
            Assert.IsNull(Read(TiffBuilder.WrapJpeg(Sample().Build(), exifAfterSos: true)));
        }

        [TestMethod]
        public void Reads_Png_eXIf() => AssertSample(Read(TiffBuilder.WrapPng(Sample().Build())));

        [TestMethod]
        public void Png_Without_Exif_Returns_Null() => Assert.IsNull(Read(TiffBuilder.WrapPng(null)));

        [TestMethod]
        public void Reads_WebP_Exif_Chunk() => AssertSample(Read(TiffBuilder.WrapWebP(Sample().Build())));

        [TestMethod]
        public void Reads_WebP_Exif_Chunk_With_Jpeg_Style_Prefix() =>
            AssertSample(Read(TiffBuilder.WrapWebP(Sample().Build(), withExifPrefix: true)));

        [TestMethod]
        public void WebP_Without_Exif_Returns_Null() => Assert.IsNull(Read(TiffBuilder.WrapWebP(null)));

        [TestMethod]
        public void Unsupported_Formats_Return_Null()
        {
            Assert.IsNull(Read("GIF89a\x01\0\x01\0\0\0\0"u8.ToArray()));
            Assert.IsNull(Read([0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
            Assert.IsNull(Read([]));
            Assert.IsNull(Read([0xFF]));
        }

        [TestMethod]
        public void Text_Is_Trimmed_And_Read_As_Utf8()
        {
            var tiff = new TiffBuilder()
                .Ascii(ExifIfd.Primary, ExifTags.Make, "NIKON CORPORATION   ")
                .Ascii(ExifIfd.Primary, ExifTags.Artist, "えもり")
                .Ascii(ExifIfd.Primary, ExifTags.Software, "   ")
                .Build();

            var exif = ExifReader.ReadTiff(tiff)!;

            Assert.AreEqual("NIKON CORPORATION", exif.GetText(ExifIfd.Primary, ExifTags.Make));
            Assert.AreEqual("えもり", exif.GetText(ExifIfd.Primary, ExifTags.Artist));
            Assert.IsNull(exif.GetText(ExifIfd.Primary, ExifTags.Software), "Blank text counts as missing");
        }

        [TestMethod]
        public void Same_Tag_Number_In_Different_Ifds_Is_Kept_Apart()
        {
            // 0x0002 is GPSLatitude in the GPS IFD, but means something else elsewhere
            var tiff = new TiffBuilder()
                .Short(ExifIfd.Primary, 0x0002, 7)
                .Rational(ExifIfd.Gps, ExifTags.GpsLatitude, (1, 1), (2, 1), (3, 1))
                .Build();

            var exif = ExifReader.ReadTiff(tiff)!;

            Assert.AreEqual(7L, exif.GetInteger(ExifIfd.Primary, 0x0002));
            Assert.AreEqual(3, exif.GetRationals(ExifIfd.Gps, ExifTags.GpsLatitude)!.Count);
        }

        [TestMethod]
        public void Zero_Denominator_Is_Treated_As_Missing()
        {
            var tiff = new TiffBuilder().Rational(ExifIfd.Exif, ExifTags.FNumber, (28, 0)).Build();

            Assert.IsNull(ExifReader.ReadTiff(tiff)!.GetRational(ExifIfd.Exif, ExifTags.FNumber));
        }

        // ------------------------------------------------------------
        // Broken data: never throw, keep what can be read
        // ------------------------------------------------------------

        [TestMethod]
        public void Value_Offset_Outside_The_Block_Skips_Only_That_Tag()
        {
            // An 8-byte RATIONAL stored at an offset far past the end
            var badValue = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(badValue, 0x7FFFFFF0);

            var tiff = new TiffBuilder()
                .Raw(ExifIfd.Primary, 0x011A, 5, 1, badValue) // XResolution, broken
                .Ascii(ExifIfd.Primary, ExifTags.Make, "Canon")
                .Build();

            var exif = ExifReader.ReadTiff(tiff)!;

            Assert.IsNull(exif.Get(ExifIfd.Primary, 0x011A));
            Assert.AreEqual("Canon", exif.GetText(ExifIfd.Primary, ExifTags.Make));
        }

        [TestMethod]
        public void Long_Text_Like_A_Comfy_Prompt_Is_Read()
        {
            // ComfyUI puts its whole node graph (often over 64KB) into one ASCII tag
            var text = "prompt:" + new string('x', 300 * 1024);
            var exif = ExifReader.ReadTiff(new TiffBuilder().Ascii(ExifIfd.Primary, ExifTags.Model, text).Build())!;

            Assert.AreEqual(text, exif.GetText(ExifIfd.Primary, ExifTags.Model));
        }

        [TestMethod]
        public void Huge_Count_Is_Skipped()
        {
            var tiff = new TiffBuilder()
                .Raw(ExifIfd.Primary, ExifTags.Make, 2, uint.MaxValue, new byte[4])
                .Short(ExifIfd.Primary, ExifTags.Orientation, 1)
                .Build();

            var exif = ExifReader.ReadTiff(tiff)!;

            Assert.IsNull(exif.Get(ExifIfd.Primary, ExifTags.Make));
            Assert.AreEqual(1L, exif.GetInteger(ExifIfd.Primary, ExifTags.Orientation));
        }

        [TestMethod]
        public void Exif_Pointer_Back_To_Ifd0_Does_Not_Loop()
        {
            // The Exif IFD pointer points at IFD0 itself (offset 8)
            var tiff = new TiffBuilder()
                .Ascii(ExifIfd.Primary, ExifTags.Make, "Canon")
                .Long(ExifIfd.Primary, ExifTags.ExifIfdPointer, 8)
                .Build();

            var exif = ExifReader.ReadTiff(tiff)!;

            Assert.AreEqual("Canon", exif.GetText(ExifIfd.Primary, ExifTags.Make));
            Assert.IsNull(exif.GetText(ExifIfd.Exif, ExifTags.Make), "IFD0 must not be read again as the Exif IFD");
        }

        [TestMethod]
        public void Truncated_Data_Does_Not_Throw()
        {
            var full = TiffBuilder.WrapJpeg(Sample().Build());

            for (int length = 0; length < full.Length; length++)
            {
                _ = Read(full[..length]); // must not throw for any cut point
            }

            var tiff = Sample().Build();
            for (int length = 0; length < tiff.Length; length++)
            {
                _ = ExifReader.ReadTiff(tiff[..length]);
            }
        }

        [TestMethod]
        public void Ifd0_Offset_Outside_The_Block_Gives_No_Entries()
        {
            var tiff = new byte[] { (byte)'I', (byte)'I', 42, 0, 0xF0, 0xFF, 0, 0 };

            var exif = ExifReader.ReadTiff(tiff);

            Assert.IsNotNull(exif);
            Assert.AreEqual(0, exif.Count);
        }

        [TestMethod]
        public void Wrong_Magic_Number_Returns_Null()
        {
            Assert.IsNull(ExifReader.ReadTiff([(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0]));
            Assert.IsNull(ExifReader.ReadTiff([(byte)'X', (byte)'X', 42, 0, 8, 0, 0, 0]));
        }
    }
}
