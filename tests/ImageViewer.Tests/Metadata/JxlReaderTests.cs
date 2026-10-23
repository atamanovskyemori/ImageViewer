using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    /// <summary>
    /// JPEG XL is built by hand here (no encoder on the test machine), following ISO/IEC 18181-1:
    /// the codestream starts with FF 0A, then SizeHeader and ImageMetadata, packed from the lowest bit.
    /// </summary>
    [TestClass]
    public class JxlReaderTests
    {
        /// <summary>Packs values from the lowest bit, like the JPEG XL codestream</summary>
        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = [0xFF, 0x0A];
            private int _bit;

            public BitWriter Bits(long value, int count)
            {
                for (int i = 0; i < count; i++, _bit++)
                {
                    if (_bit % 8 == 0)
                    {
                        _bytes.Add(0);
                    }
                    if (((value >> i) & 1) != 0)
                    {
                        _bytes[^1] |= (byte)(1 << (_bit % 8));
                    }
                }
                return this;
            }

            public byte[] ToArray() => [.. _bytes, 0, 0, 0, 0]; // a little padding, like the rest of a real file
        }

        /// <summary>Small SizeHeader: both sides multiples of 8 up to 256. Then ImageMetadata with the orientation</summary>
        private static byte[] SmallCodestream(int width, int height, int orientation = 1)
        {
            var bits = new BitWriter()
                .Bits(1, 1)                  // small
                .Bits(height / 8 - 1, 5)     // ysize_div8_minus_1
                .Bits(0, 3)                  // ratio: none
                .Bits(width / 8 - 1, 5);     // xsize_div8_minus_1
            return Metadata(bits, orientation).ToArray();
        }

        private static BitWriter Metadata(BitWriter bits, int orientation) =>
            orientation == 1
                ? bits.Bits(1, 1)                                   // all_default
                : bits.Bits(0, 1).Bits(1, 1).Bits(orientation - 1, 3); // extra_fields, orientation - 1

        private static byte[] Box(string type, byte[] body)
        {
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        private static readonly byte[] Signature = [0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];

        private static byte[] Container(params byte[][] boxes) =>
            [.. Signature, .. Box("ftyp", [.. "jxl "u8.ToArray(), 0, 0, 0, 0, .. "jxl "u8.ToArray()]), .. boxes.SelectMany(b => b)];

        private static byte[] Tiff() => new TiffBuilder()
            .Ascii(ExifIfd.Primary, ExifTags.Make, "FUJIFILM")
            .Ascii(ExifIfd.Primary, ExifTags.Model, "X-T5")
            .Ascii(ExifIfd.Exif, ExifTags.DateTimeOriginal, "2026:09:12 16:20:00")
            .Build();

        /// <summary>Exif box body: 4-byte offset to the TIFF header, "Exif\0\0", TIFF</summary>
        private static byte[] ExifBody() => [0, 0, 0, 6, .. "Exif\0\0"u8.ToArray(), .. Tiff()];

        private static byte[] Brotli(byte[] data)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Optimal))
            {
                brotli.Write(data);
            }
            return output.ToArray();
        }

        private static JxlInfo? Read(byte[] file)
        {
            using var stream = new MemoryStream(file, writable: false);
            return JxlReader.Read(stream);
        }

        private static ExifData? ReadExif(byte[] file)
        {
            using var stream = new MemoryStream(file, writable: false);
            return ExifReader.Read(stream);
        }

        private static void AssertExif(ExifData? exif)
        {
            Assert.IsNotNull(exif);
            Assert.AreEqual("FUJIFILM", exif.GetText(ExifIfd.Primary, ExifTags.Make));
            Assert.AreEqual("X-T5", exif.GetText(ExifIfd.Primary, ExifTags.Model));
            Assert.AreEqual("2026:09:12 16:20:00", exif.GetText(ExifIfd.Exif, ExifTags.DateTimeOriginal));
        }

        [TestMethod]
        public void Bare_Codestream_Gives_Size_But_No_Exif()
        {
            var file = SmallCodestream(width: 64, height: 32);

            var info = Read(file);
            Assert.IsNotNull(info);
            Assert.AreEqual(64, info.Width);
            Assert.AreEqual(32, info.Height);
            Assert.IsNull(info.Exif);
            Assert.IsNull(ReadExif(file));
        }

        [TestMethod]
        [DataRow(1, 64, 32)]
        [DataRow(3, 64, 32)] // 180°
        [DataRow(6, 32, 64)] // 90° clockwise
        [DataRow(8, 32, 64)] // 90° counter-clockwise
        public void Orientation_Turning_By_90_Degrees_Swaps_Width_And_Height(int orientation, int width, int height)
        {
            var info = Read(SmallCodestream(width: 64, height: 32, orientation));

            Assert.AreEqual(width, info?.Width);
            Assert.AreEqual(height, info?.Height);
        }

        [TestMethod]
        public void Large_Size_Uses_The_Longer_Fields()
        {
            // not small; height: selector 1 (13 bits) = 3023 + 1; width: selector 2 (18 bits) = 4031 + 1
            var bits = new BitWriter()
                .Bits(0, 1)
                .Bits(1, 2).Bits(3023, 13)
                .Bits(0, 3)
                .Bits(2, 2).Bits(4031, 18);
            var info = Read(Metadata(bits, 1).ToArray());

            Assert.AreEqual(4032, info?.Width);
            Assert.AreEqual(3024, info?.Height);
        }

        [TestMethod]
        [DataRow(1, 1000, 1000)]  // 1:1
        [DataRow(2, 1200, 1000)]  // 12:10
        [DataRow(3, 1333, 1000)]  // 4:3
        [DataRow(4, 1500, 1000)]  // 3:2
        [DataRow(5, 1777, 1000)]  // 16:9
        [DataRow(6, 1250, 1000)]  // 5:4
        [DataRow(7, 2000, 1000)]  // 2:1
        public void Width_From_The_Aspect_Ratio(int ratio, int width, int height)
        {
            var bits = new BitWriter()
                .Bits(0, 1)
                .Bits(1, 2).Bits(999, 13) // height: selector 1 (13 bits) = 999 + 1
                .Bits(ratio, 3);
            var info = Read(Metadata(bits, 1).ToArray());

            Assert.AreEqual(width, info?.Width, $"got {info?.Width}x{info?.Height}");
            Assert.AreEqual(height, info?.Height);
        }

        [TestMethod]
        public void Smallest_Size_Field_Holds_Up_To_512()
        {
            // not small; height: selector 0 (9 bits) = 499 + 1; width: selector 0 = 511 + 1
            var bits = new BitWriter()
                .Bits(0, 1)
                .Bits(0, 2).Bits(499, 9)
                .Bits(0, 3)
                .Bits(0, 2).Bits(511, 9);
            var info = Read(Metadata(bits, 1).ToArray());

            Assert.AreEqual(512, info?.Width);
            Assert.AreEqual(500, info?.Height);
        }

        [TestMethod]
        public void Container_Exif_Box_Is_Read()
        {
            var file = Container(Box("Exif", ExifBody()), Box("jxlc", SmallCodestream(128, 96)));

            AssertExif(ReadExif(file));
            var info = Read(file);
            Assert.AreEqual(128, info?.Width);
            Assert.AreEqual(96, info?.Height);
        }

        [TestMethod]
        public void Brotli_Compressed_Exif_Box_Is_Read()
        {
            var file = Container(Box("brob", [.. "Exif"u8.ToArray(), .. Brotli(ExifBody())]), Box("jxlc", SmallCodestream(128, 96)));

            AssertExif(ReadExif(file));
        }

        [TestMethod]
        public void Split_Codestream_Gives_Size()
        {
            // jxlp: the codestream in parts, each with a 4-byte index first
            var codestream = SmallCodestream(256, 192);
            var file = Container(
                Box("jxlp", [0, 0, 0, 0, .. codestream[..6]]),
                Box("jxlp", [0x80, 0, 0, 1, .. codestream[6..]]));

            var info = Read(file);
            Assert.AreEqual(256, info?.Width);
            Assert.AreEqual(192, info?.Height);
        }

        [TestMethod]
        public void Exif_After_The_Codestream_Is_Still_Found()
        {
            var file = Container(Box("jxlc", SmallCodestream(64, 64)), Box("Exif", ExifBody()));

            AssertExif(ReadExif(file));
        }

        [TestMethod]
        public void Broken_Brotli_Gives_No_Exif_Without_Throwing()
        {
            var file = Container(Box("brob", [.. "Exif"u8.ToArray(), 1, 2, 3, 4, 5]), Box("jxlc", SmallCodestream(64, 64)));

            Assert.IsNull(ReadExif(file));
            Assert.AreEqual(64, Read(file)?.Width);
        }

        [TestMethod]
        public void Truncated_Files_Do_Not_Throw()
        {
            var file = Container(Box("Exif", ExifBody()), Box("jxlc", SmallCodestream(128, 96)));
            for (int length = 0; length < file.Length; length += 5)
            {
                var cut = file[..length];
                _ = Read(cut);
                _ = ReadExif(cut);
            }

            Assert.IsNull(Read([0xFF, 0x0A])?.Width, "no size header at all");
        }

        [TestMethod]
        public void Info_Panel_Shows_Format_Size_And_Camera()
        {
            var path = Path.Combine(Path.GetTempPath(), "ImageViewer.Tests", Guid.NewGuid().ToString("N") + ".jxl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Container(Box("Exif", ExifBody()), Box("jxlc", SmallCodestream(128, 96, orientation: 6))));
            try
            {
                var info = ImageInfoLoader.Load(path);

                var file = info.Sections.Single(s => s.Title == "ファイル").Rows;
                Assert.AreEqual("JPEG XL", file.Single(r => r.Label == "形式").Value);
                Assert.AreEqual("96 × 128", file.Single(r => r.Label == "ピクセル数").Value);

                var shot = info.Sections.Single(s => s.Title == "撮影").Rows;
                Assert.AreEqual("FUJIFILM", shot.Single(r => r.Label == "メーカー").Value);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
