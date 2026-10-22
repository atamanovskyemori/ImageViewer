using System.Text;
using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class ExifTextTests
    {
        private static byte[] ShiftJis(string text)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetBytes(text);
        }

        [TestMethod]
        public void Ascii_Is_Read_As_Is()
        {
            Assert.AreEqual("Adobe Photoshop 25.0", ExifText.Decode("Adobe Photoshop 25.0"u8));
        }

        [TestMethod]
        public void Utf8_Is_Read()
        {
            Assert.AreEqual("写真アプリ 2.1", ExifText.Decode(Encoding.UTF8.GetBytes("写真アプリ 2.1")));
        }

        [TestMethod]
        [DataRow("デジカメ編集ソフト Ver.3")]
        [DataRow("ＰｈｏｔｏＳｔｕｄｉｏ")]
        [DataRow("ｶﾒﾗ")]
        public void Shift_Jis_Is_Read_Without_Garbled_Characters(string text)
        {
            var decoded = ExifText.Decode(ShiftJis(text));

            Assert.AreEqual(text, decoded);
            Assert.IsFalse(decoded.Contains('�'));
        }

        [TestMethod]
        public void Utf8_Cut_In_The_Middle_Of_A_Character_Drops_Only_The_Broken_End()
        {
            var bytes = Encoding.UTF8.GetBytes("編集アプリ");
            var cut = bytes[..^1]; // the last character loses its final byte

            Assert.AreEqual("編集アプ", ExifText.Decode(cut));
        }

        [TestMethod]
        public void Empty_Is_Empty()
        {
            Assert.AreEqual(string.Empty, ExifText.Decode([]));
        }

        [TestMethod]
        public void Undecodable_Bytes_Do_Not_Throw()
        {
            // Invalid as UTF-8 and as Shift_JIS (0x80 / 0xA0 / 0xFD-0xFF are not lead bytes)
            byte[] bytes = [0x41, 0x80, 0xFF, 0x42, 0xFD, 0x43];

            var decoded = ExifText.Decode(bytes);

            StringAssert.StartsWith(decoded, "A");
        }

        [TestMethod]
        public void Software_Tag_Written_In_Shift_Jis_Is_Shown_Correctly()
        {
            var software = ShiftJis("画像編集ソフト");
            byte[] value = [.. software, 0];
            var tiff = new TiffBuilder()
                .Raw(ExifIfd.Primary, ExifTags.Software, 2, (uint)value.Length, value)
                .Build();

            var exif = ExifReader.ReadTiff(tiff);

            Assert.AreEqual("画像編集ソフト", exif?.GetText(ExifIfd.Primary, ExifTags.Software));
        }

        [TestMethod]
        public void User_Comment_Without_Encoding_Reads_Shift_Jis()
        {
            byte[] bytes = [0, 0, 0, 0, 0, 0, 0, 0, .. ShiftJis("旅行の写真")];

            Assert.AreEqual("旅行の写真", GenerationInfo.DecodeUserComment(bytes));
        }
    }
}
