using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class GenerationInfoTests
    {
        private const string Sample =
            "a photo of a cat, sitting on a wooden table\n" +
            "soft lighting\n" +
            "Negative prompt: blurry, low quality\n" +
            "watermark\n" +
            "Steps: 20, Sampler: Euler a, Schedule type: Karras, CFG scale: 7, Seed: 1234567890, " +
            "Size: 512x768, Model hash: abcdef1234, Model: sample_model_v1, " +
            "Lora hashes: \"cat_style: 0123abcd, light: 4567ef\", Version: v1.10.1";

        // ------------------------------------------------------------
        // WebUI "parameters"
        // ------------------------------------------------------------

        [TestMethod]
        public void Parses_Prompt_Negative_And_Parameters()
        {
            var info = GenerationInfo.Parse(Sample, GenerationSources.PngParameters)!;

            Assert.AreEqual("a photo of a cat, sitting on a wooden table\nsoft lighting", info.Prompt);
            Assert.AreEqual("blurry, low quality\nwatermark", info.NegativePrompt, "Negative prompt can span lines");
            Assert.AreEqual("20", info.Get("Steps"));
            Assert.AreEqual("Euler a", info.Get("Sampler"));
            Assert.AreEqual("7", info.Get("cfg scale"), "Keys are case-insensitive");
            Assert.AreEqual("1234567890", info.Get("Seed"));
            Assert.AreEqual("512x768", info.Get("Size"));
            Assert.AreEqual("sample_model_v1", info.Get("Model"));
            Assert.AreEqual("v1.10.1", info.Get("Version"));
            Assert.AreEqual(GenerationSources.PngParameters, info.Source);
        }

        [TestMethod]
        public void Quoted_Values_Keep_Their_Commas()
        {
            var info = GenerationInfo.Parse(Sample, GenerationSources.PngParameters)!;

            Assert.AreEqual("cat_style: 0123abcd, light: 4567ef", info.Get("Lora hashes"));
        }

        [TestMethod]
        public void Parameters_Keep_Their_Order()
        {
            var info = GenerationInfo.Parse(Sample, GenerationSources.PngParameters)!;

            Assert.AreEqual("Steps", info.Parameters[0].Key);
            Assert.AreEqual("Version", info.Parameters[^1].Key);
        }

        [TestMethod]
        public void Without_Negative_Prompt()
        {
            var info = GenerationInfo.Parse(
                "a red apple\nSteps: 30, Sampler: DPM++ 2M, CFG scale: 5.5, Seed: 1",
                GenerationSources.PngParameters)!;

            Assert.AreEqual("a red apple", info.Prompt);
            Assert.IsNull(info.NegativePrompt);
            Assert.AreEqual("5.5", info.Get("CFG scale"));
        }

        [TestMethod]
        public void Without_Parameters_Line_Everything_Is_The_Prompt()
        {
            // Fewer than three "key: value" pairs on the last line: not a parameters line
            var info = GenerationInfo.Parse("a lighthouse at dusk\nstyle: watercolor", GenerationSources.PngParameters)!;

            Assert.AreEqual("a lighthouse at dusk\nstyle: watercolor", info.Prompt);
            Assert.AreEqual(0, info.Parameters.Count);
        }

        [TestMethod]
        public void Empty_Prompt_With_Parameters()
        {
            var info = GenerationInfo.Parse("Steps: 20, Sampler: Euler, Seed: 5", GenerationSources.PngParameters)!;

            Assert.IsNull(info.Prompt);
            Assert.AreEqual(3, info.Parameters.Count);
        }

        [TestMethod]
        public void Crlf_Is_Accepted()
        {
            var info = GenerationInfo.Parse(
                "a boat\r\nNegative prompt: fog\r\nSteps: 20, Sampler: Euler, Seed: 5",
                GenerationSources.PngParameters)!;

            Assert.AreEqual("a boat", info.Prompt);
            Assert.AreEqual("fog", info.NegativePrompt);
        }

        [TestMethod]
        public void Blank_Text_Returns_Null()
        {
            Assert.IsNull(GenerationInfo.Parse(null, GenerationSources.PngParameters));
            Assert.IsNull(GenerationInfo.Parse("  \n ", GenerationSources.PngParameters));
        }

        // ------------------------------------------------------------
        // EXIF UserComment
        // ------------------------------------------------------------

        private static byte[] UserComment(string header, byte[] body) => [.. Encoding.ASCII.GetBytes(header), .. body];

        [TestMethod]
        public void UserComment_Unicode_Big_Endian()
        {
            var bytes = UserComment("UNICODE\0", Encoding.BigEndianUnicode.GetBytes("a cat ねこ"));

            Assert.AreEqual("a cat ねこ", GenerationInfo.DecodeUserComment(bytes));
        }

        [TestMethod]
        public void UserComment_Unicode_Little_Endian()
        {
            var bytes = UserComment("UNICODE\0", Encoding.Unicode.GetBytes("a cat ねこ"));

            Assert.AreEqual("a cat ねこ", GenerationInfo.DecodeUserComment(bytes));
        }

        [TestMethod]
        public void UserComment_Unicode_With_Bom()
        {
            var bytes = UserComment("UNICODE\0", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("abc")]);

            Assert.AreEqual("abc", GenerationInfo.DecodeUserComment(bytes));
        }

        [TestMethod]
        public void UserComment_Ascii_And_Undefined()
        {
            Assert.AreEqual("hello", GenerationInfo.DecodeUserComment(UserComment("ASCII\0\0\0", Encoding.ASCII.GetBytes("hello\0"))));
            Assert.AreEqual("hello", GenerationInfo.DecodeUserComment(UserComment("\0\0\0\0\0\0\0\0", Encoding.ASCII.GetBytes("hello   "))));
        }

        [TestMethod]
        public void UserComment_Empty_Or_Short_Is_Null()
        {
            Assert.IsNull(GenerationInfo.DecodeUserComment(null));
            Assert.IsNull(GenerationInfo.DecodeUserComment(new byte[8]));
            Assert.IsNull(GenerationInfo.DecodeUserComment(UserComment("ASCII\0\0\0", new byte[16])), "Cameras often write only zeros");
        }

        // ------------------------------------------------------------
        // PNG text chunks
        // ------------------------------------------------------------

        private static byte[] Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            return [.. length, .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];
        }

        private static byte[] Png(params byte[][] chunks)
        {
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            png = [.. png, .. Chunk("IHDR", new byte[13])];
            foreach (var chunk in chunks)
            {
                png = [.. png, .. chunk];
            }
            return [.. png, .. Chunk("IEND", [])];
        }

        private static byte[] Zlib(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data);
            }
            return output.ToArray();
        }

        private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

        private static IReadOnlyList<PngText> ReadPng(byte[] png)
        {
            using var stream = new MemoryStream(png, writable: false);
            return PngTextReader.Read(stream);
        }

        [TestMethod]
        public void Reads_tEXt()
        {
            var texts = ReadPng(Png(Chunk("tEXt", Latin1("parameters\0a photo of a cat"))));

            Assert.AreEqual(1, texts.Count);
            Assert.AreEqual(new PngText("parameters", "a photo of a cat"), texts[0]);
        }

        [TestMethod]
        public void Reads_zTXt()
        {
            byte[] data = [.. Latin1("Comment\0"), 0, .. Zlib(Latin1("compressed text"))];

            var texts = ReadPng(Png(Chunk("zTXt", data)));

            Assert.AreEqual(new PngText("Comment", "compressed text"), texts[0]);
        }

        [TestMethod]
        public void Reads_iTXt_Plain_And_Compressed()
        {
            var utf8 = Encoding.UTF8.GetBytes("ねこの写真");
            byte[] plain = [.. Latin1("parameters\0"), 0, 0, .. Latin1("ja\0"), .. Encoding.UTF8.GetBytes("パラメータ"), 0, .. utf8];
            byte[] compressed = [.. Latin1("Description\0"), 1, 0, 0, 0, .. Zlib(utf8)];

            var texts = ReadPng(Png(Chunk("iTXt", plain), Chunk("iTXt", compressed)));

            Assert.AreEqual(new PngText("parameters", "ねこの写真"), texts[0]);
            Assert.AreEqual(new PngText("Description", "ねこの写真"), texts[1]);
        }

        [TestMethod]
        public void Reads_Text_After_Image_Data()
        {
            var texts = ReadPng(Png(Chunk("IDAT", new byte[100]), Chunk("tEXt", Latin1("Software\0sample"))));

            Assert.AreEqual(new PngText("Software", "sample"), texts[0]);
        }

        [TestMethod]
        public void Broken_Chunks_Are_Skipped()
        {
            var texts = ReadPng(Png(
                Chunk("tEXt", Latin1("no keyword separator")),
                Chunk("zTXt", [.. Latin1("Bad\0"), 0, 1, 2, 3]),       // not zlib
                Chunk("tEXt", Latin1("Good\0ok"))));

            Assert.AreEqual(1, texts.Count);
            Assert.AreEqual("Good", texts[0].Keyword);
        }

        [TestMethod]
        public void Truncated_Png_Does_Not_Throw()
        {
            var png = Png(Chunk("tEXt", Latin1("parameters\0a photo of a cat")));

            for (int length = 0; length < png.Length; length++)
            {
                _ = ReadPng(png[..length]);
            }
        }

        [TestMethod]
        public void Not_A_Png_Returns_Empty()
        {
            Assert.AreEqual(0, ReadPng("GIF89a\0\0\0\0"u8.ToArray()).Count);
        }

        // ------------------------------------------------------------
        // Info panel rows
        // ------------------------------------------------------------

        [TestMethod]
        public void Builder_Adds_Generation_Section_And_Other_Png_Texts()
        {
            var facts = new FileFacts(@"C:\x\a.png", 100, null, "PNG", 512, 768, false);
            var generation = GenerationInfo.Parse(Sample, GenerationSources.PngParameters);
            var texts = new[] { new PngText("parameters", Sample), new PngText("Software", "sample app") };

            var info = ImageInfoBuilder.Build(facts, null, generation, texts);

            CollectionAssert.AreEqual(new[] { "ファイル", "生成情報", "PNG テキスト" }, info.Sections.Select(s => s.Title).ToArray());

            var rows = info.Sections[1].Rows;
            Assert.AreEqual("プロンプト", rows[0].Label);
            Assert.AreEqual("ネガティブプロンプト", rows[1].Label);
            Assert.IsTrue(rows.Any(r => r.Label == "シード" && r.Value == "1234567890"));
            Assert.IsTrue(rows.Any(r => r.Label == "Lora hashes"), "Unknown keys are shown as they are");

            // "parameters" is already shown as the generation section
            Assert.AreEqual(1, info.Sections[2].Rows.Count);
            Assert.AreEqual("Software", info.Sections[2].Rows[0].Label);

            Assert.AreSame(generation, info.Generation);
        }

        [TestMethod]
        public void Very_Long_Png_Text_Is_Shortened()
        {
            var facts = new FileFacts(@"C:\x\a.png", 100, null, "PNG", 1, 1, false);
            var texts = new[] { new PngText("workflow", new string('x', 5000)) };

            var info = ImageInfoBuilder.Build(facts, null, null, texts);

            Assert.IsTrue(info.Sections[1].Rows[0].Value.Length < 2100);
            Assert.IsTrue(info.Sections[1].Rows[0].Value.EndsWith("（以下省略）"));
        }
    }
}
