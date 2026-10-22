using System.Buffers.Binary;
using System.IO;
using System.Text;
using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class HeifReaderTests
    {
        private static byte[] Tiff() => new TiffBuilder(bigEndian: true)
            .Ascii(ExifIfd.Primary, ExifTags.Make, "Apple")
            .Ascii(ExifIfd.Primary, ExifTags.Model, "iPhone 15 Pro")
            .Ascii(ExifIfd.Exif, ExifTags.DateTimeOriginal, "2026:10:01 09:30:00")
            .Ascii(ExifIfd.Gps, ExifTags.GpsLatitudeRef, "N")
            .Rational(ExifIfd.Gps, ExifTags.GpsLatitude, (35, 1), (39, 1), (312, 10))
            .Build();

        // ------------------------------------------------------------
        // A minimal HEIF writer: ftyp + meta (pitm, iinf, iloc, [idat], iprp) + mdat
        // ------------------------------------------------------------

        private static byte[] Box(string type, params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }

        private static byte[] FullBox(string type, byte version, uint flags, params byte[][] parts) =>
            Box(type, [[version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], .. parts]);

        private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

        private static byte[] U32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        private static byte[] Infe(int id, string type) =>
            FullBox("infe", 2, 0, U16(id), U16(0), Encoding.ASCII.GetBytes(type), [0]);

        /// <summary>Exif item payload: 4-byte offset to the TIFF header, then (optionally) "Exif\0\0", then TIFF</summary>
        private static byte[] ExifPayload(byte[] tiff, bool withExifPrefix = true) =>
            withExifPrefix ? [.. U32(6), .. "Exif\0\0"u8.ToArray(), .. tiff] : [.. U32(0), .. tiff];

        /// <param name="inIdat">true: put the Exif item in idat (construction_method 1); false: in mdat (file offset)</param>
        private static byte[] Heif(byte[]? exifPayload, string brand = "heic", int width = 4032, int height = 3024,
            int? rotation = null, bool inIdat = false)
        {
            byte[] Meta(long exifFileOffset)
            {
                var ipco = new List<byte[]> { FullBox("ispe", 0, 0, U32(width), U32(height)) };
                if (rotation is { } r)
                {
                    ipco.Add(Box("irot", [(byte)(r / 90)]));
                }
                // item 1 (primary) gets properties 1.. ; the 0x80 bit marks "essential"
                var assoc = ipco.Select((_, i) => (byte)(0x80 | (i + 1))).ToArray();
                var ipma = FullBox("ipma", 0, 0, U32(1), U16(1), [(byte)assoc.Length], assoc);

                var parts = new List<byte[]>
                {
                    FullBox("pitm", 0, 0, U16(1)),
                    FullBox("iinf", 0, 0, U16(exifPayload is null ? 1 : 2), Infe(1, "hvc1"), exifPayload is null ? [] : Infe(2, "Exif")),
                };

                if (exifPayload is not null)
                {
                    // version 1: offset_size 4, length_size 4, base_offset_size 0, index_size 0
                    parts.Add(FullBox("iloc", 1, 0, [0x44, 0x00], U16(1),
                        U16(2), U16(inIdat ? 1 : 0), U16(0), U16(1), U32(inIdat ? 0 : exifFileOffset), U32(exifPayload.Length)));
                    if (inIdat)
                    {
                        parts.Add(Box("idat", exifPayload));
                    }
                }

                parts.Add(Box("iprp", Box("ipco", [.. ipco.SelectMany(b => b)]), ipma));
                return FullBox("meta", 0, 0, [.. parts.SelectMany(b => b)]);
            }

            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes(brand), U32(0), "mif1"u8.ToArray(), Encoding.ASCII.GetBytes(brand));
            var meta = Meta(0);
            long exifOffset = ftyp.Length + meta.Length + 8; // the payload is right after the mdat header
            meta = Meta(exifOffset);                         // same size, now with the real offset
            var mdat = Box("mdat", inIdat || exifPayload is null ? [1, 2, 3] : exifPayload);
            return [.. ftyp, .. meta, .. mdat];
        }

        private static ExifData? ReadExif(byte[] file)
        {
            using var stream = new MemoryStream(file, writable: false);
            return ExifReader.Read(stream);
        }

        private static HeifInfo? ReadHeif(byte[] file)
        {
            using var stream = new MemoryStream(file, writable: false);
            return HeifReader.Read(stream);
        }

        private static void AssertExif(ExifData? exif)
        {
            Assert.IsNotNull(exif);
            Assert.AreEqual("Apple", exif.GetText(ExifIfd.Primary, ExifTags.Make));
            Assert.AreEqual("iPhone 15 Pro", exif.GetText(ExifIfd.Primary, ExifTags.Model));
            Assert.AreEqual("2026:10:01 09:30:00", exif.GetText(ExifIfd.Exif, ExifTags.DateTimeOriginal));
            Assert.AreEqual("N", exif.GetText(ExifIfd.Gps, ExifTags.GpsLatitudeRef));
        }

        [TestMethod]
        public void Exif_In_Mdat_Is_Read()
        {
            AssertExif(ReadExif(Heif(ExifPayload(Tiff()))));
        }

        [TestMethod]
        public void Exif_In_Idat_Is_Read()
        {
            AssertExif(ReadExif(Heif(ExifPayload(Tiff()), inIdat: true)));
        }

        [TestMethod]
        public void Exif_Without_Exif_Prefix_Is_Read()
        {
            AssertExif(ReadExif(Heif(ExifPayload(Tiff(), withExifPrefix: false))));
        }

        [TestMethod]
        public void Avif_Exif_Is_Read_Too()
        {
            AssertExif(ReadExif(Heif(ExifPayload(Tiff()), brand: "avif")));
        }

        [TestMethod]
        public void Size_And_Rotation_Of_The_Primary_Image_Are_Read()
        {
            var info = ReadHeif(Heif(ExifPayload(Tiff()), width: 4032, height: 3024, rotation: 270));

            Assert.IsNotNull(info);
            Assert.AreEqual(4032, info.Width);
            Assert.AreEqual(3024, info.Height);
            Assert.AreEqual(270, info.Rotation);
            Assert.AreEqual("heic", info.Brand);
        }

        [TestMethod]
        public void File_Without_Exif_Still_Gives_Size()
        {
            var file = Heif(null);

            Assert.IsNull(ReadExif(file));
            var info = ReadHeif(file);
            Assert.IsNotNull(info);
            Assert.IsNull(info.Exif);
            Assert.AreEqual(4032, info.Width);
            Assert.AreEqual(0, info.Rotation);
        }

        [TestMethod]
        public void Exif_Offset_Pointing_Past_The_End_Gives_No_Exif()
        {
            var file = Heif(ExifPayload(Tiff()));
            var broken = file[..^20]; // cut the end of the Exif item off

            Assert.IsNull(ReadExif(broken));
            Assert.AreEqual(4032, ReadHeif(broken)?.Width, "the size in meta is still readable");
        }

        [TestMethod]
        public void Truncated_Meta_Does_Not_Throw()
        {
            var file = Heif(ExifPayload(Tiff()));
            for (int length = 12; length < 200; length += 7)
            {
                var cut = file[..length];
                Assert.IsNull(ReadExif(cut));
                _ = ReadHeif(cut); // must not throw
            }
        }

        [TestMethod]
        public void Garbage_After_Ftyp_Does_Not_Throw()
        {
            var ftyp = Box("ftyp", "heic"u8.ToArray(), U32(0), "mif1"u8.ToArray());
            var random = new Random(1);
            for (int i = 0; i < 50; i++)
            {
                var garbage = new byte[random.Next(0, 300)];
                random.NextBytes(garbage);
                byte[] file = [.. ftyp, .. garbage];
                _ = ReadExif(file);
                _ = ReadHeif(file);
            }
        }

        [TestMethod]
        [DataRow("heic", true)]
        [DataRow("heix", true)]
        [DataRow("mif1", true)]
        [DataRow("avif", true)]
        [DataRow("isom", false)] // an MP4 video
        [DataRow("qt  ", false)]
        public void Heif_Brands_Are_Recognized(string brand, bool expected)
        {
            var header = Box("ftyp", Encoding.ASCII.GetBytes(brand), U32(0));
            Assert.AreEqual(expected, HeifReader.IsHeif(header));
        }

        [TestMethod]
        public void Info_Panel_Shows_Camera_Info_Format_And_Rotated_Size()
        {
            var path = Path.Combine(Path.GetTempPath(), "ImageViewer.Tests", Guid.NewGuid().ToString("N") + ".heic");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Heif(ExifPayload(Tiff()), width: 4032, height: 3024, rotation: 90));
            try
            {
                var info = ImageInfoLoader.Load(path);

                var file = info.Sections.Single(s => s.Title == "ファイル").Rows;
                Assert.AreEqual("HEIC", file.Single(r => r.Label == "形式").Value);
                Assert.AreEqual("3024 × 4032", file.Single(r => r.Label == "ピクセル数").Value, "irot 90 turns it upright");

                var shot = info.Sections.Single(s => s.Title == "撮影").Rows;
                Assert.AreEqual("Apple", shot.Single(r => r.Label == "メーカー").Value);
                Assert.AreEqual("iPhone 15 Pro", shot.Single(r => r.Label == "機種").Value);
                Assert.IsTrue(info.Sections.Any(s => s.Title == "位置情報"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
