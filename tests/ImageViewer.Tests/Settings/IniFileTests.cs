using ImageViewer.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Settings
{
    [TestClass]
    public class IniFileTests
    {
        [TestMethod]
        public void Parse_Reads_Sections_And_Keys()
        {
            var ini = IniFile.Parse("[Window]\r\nLeft=10\r\nTop = 20 \r\n\r\n[Sort]\r\nKey=Size\r\n");

            Assert.AreEqual("10", ini.Get("Window", "Left"));
            Assert.AreEqual("20", ini.Get("Window", "Top"), "Spaces around key and value should be trimmed");
            Assert.AreEqual("Size", ini.Get("Sort", "Key"));
        }

        [TestMethod]
        public void Parse_Is_Case_Insensitive_For_Sections_And_Keys()
        {
            var ini = IniFile.Parse("[window]\nleft=10\n");

            Assert.AreEqual("10", ini.Get("Window", "Left"));
        }

        [TestMethod]
        public void Parse_Ignores_Comments_And_Broken_Lines()
        {
            var ini = IniFile.Parse("; comment\n# another\n[Window]\nno equal sign\n=no key\nLeft=10\n");

            Assert.AreEqual("10", ini.Get("Window", "Left"));
            Assert.IsNull(ini.Get("Window", "no equal sign"));
            Assert.IsNull(ini.Get("Window", ""));
        }

        [TestMethod]
        public void Parse_Last_Duplicate_Key_Wins()
        {
            var ini = IniFile.Parse("[Window]\nLeft=10\nLeft=30\n");

            Assert.AreEqual("30", ini.Get("Window", "Left"));
        }

        [TestMethod]
        public void Value_May_Contain_Equal_Sign()
        {
            var ini = IniFile.Parse("[A]\nKey=a=b\n");

            Assert.AreEqual("a=b", ini.Get("A", "Key"));
        }

        [TestMethod]
        public void Get_Returns_Null_When_Missing()
        {
            var ini = IniFile.Parse("[A]\nKey=1\n");

            Assert.IsNull(ini.Get("A", "Other"));
            Assert.IsNull(ini.Get("B", "Key"));
        }

        [TestMethod]
        public void ToString_RoundTrips_And_Keeps_Order()
        {
            var ini = new IniFile();
            ini.Set("Window", "Left", "10");
            ini.Set("Window", "Top", "20");
            ini.Set("Sort", "Key", "Name");
            ini.Set("Window", "Left", "15"); // overwrite keeps position

            var text = ini.ToString();
            Assert.AreEqual("[Window]\r\nLeft=15\r\nTop=20\r\n\r\n[Sort]\r\nKey=Name\r\n", text);

            var parsed = IniFile.Parse(text);
            Assert.AreEqual("15", parsed.Get("Window", "Left"));
            Assert.AreEqual("Name", parsed.Get("Sort", "Key"));
        }

        [TestMethod]
        public void Keys_Without_Section_Are_Written_First()
        {
            var ini = new IniFile();
            ini.Set("A", "Key", "1");
            ini.Set("", "Global", "2");

            var parsed = IniFile.Parse(ini.ToString());

            Assert.AreEqual("2", parsed.Get("", "Global"));
            Assert.IsNull(parsed.Get("A", "Global"), "Global key must not end up inside section A");
        }

        [TestMethod]
        public void Set_Replaces_Newlines_In_Value()
        {
            var ini = new IniFile();
            ini.Set("A", "Key", "line1\r\nline2");

            var parsed = IniFile.Parse(ini.ToString());

            Assert.AreEqual("line1  line2", parsed.Get("A", "Key"));
        }

        [TestMethod]
        public void Parse_Handles_Unicode_Values()
        {
            var ini = IniFile.Parse("[A]\nPath=E:\\画像\\フォルダ\n");

            Assert.AreEqual("E:\\画像\\フォルダ", ini.Get("A", "Path"));
        }
    }
}
