using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class ExifFormatTests
    {
        [TestMethod]
        public void ExposureTime()
        {
            Assert.AreEqual("1/250 秒", ExifFormat.ExposureTime(new Rational(1, 250)));
            Assert.AreEqual("1/250 秒", ExifFormat.ExposureTime(new Rational(10, 2500)), "Not reduced fractions");
            Assert.AreEqual("1/3 秒", ExifFormat.ExposureTime(new Rational(3, 10)));
            Assert.AreEqual("2.5 秒", ExifFormat.ExposureTime(new Rational(25, 10)));
            Assert.AreEqual("30 秒", ExifFormat.ExposureTime(new Rational(30, 1)));
            Assert.IsNull(ExifFormat.ExposureTime(new Rational(0, 1)));
            Assert.IsNull(ExifFormat.ExposureTime(new Rational(1, 0)));
            Assert.IsNull(ExifFormat.ExposureTime(null));
        }

        [TestMethod]
        public void FNumber()
        {
            Assert.AreEqual("F2.8", ExifFormat.FNumber(new Rational(28, 10)));
            Assert.AreEqual("F8", ExifFormat.FNumber(new Rational(8, 1)));
            Assert.AreEqual("F1.4", ExifFormat.FNumber(new Rational(14, 10)));
            Assert.IsNull(ExifFormat.FNumber(new Rational(0, 1)));
        }

        [TestMethod]
        public void FocalLength()
        {
            Assert.AreEqual("50 mm（35mm換算 75 mm）", ExifFormat.FocalLength(new Rational(50, 1), 75));
            Assert.AreEqual("4.3 mm", ExifFormat.FocalLength(new Rational(43, 10), null));
            Assert.AreEqual("4.3 mm", ExifFormat.FocalLength(new Rational(43, 10), 0), "0 means unknown");
            Assert.AreEqual("35mm換算 26 mm", ExifFormat.FocalLength(null, 26));
            Assert.IsNull(ExifFormat.FocalLength(null, null));
        }

        [TestMethod]
        public void ExposureBias()
        {
            Assert.AreEqual("-0.7 EV", ExifFormat.ExposureBias(new Rational(-2, 3)));
            Assert.AreEqual("+0.3 EV", ExifFormat.ExposureBias(new Rational(1, 3)));
            Assert.AreEqual("+1 EV", ExifFormat.ExposureBias(new Rational(1, 1)));
            Assert.AreEqual("0 EV", ExifFormat.ExposureBias(new Rational(0, 1)));
            Assert.IsNull(ExifFormat.ExposureBias(new Rational(1, 0)));
        }

        [TestMethod]
        public void Flash()
        {
            Assert.AreEqual("発光なし", ExifFormat.Flash(0x10));
            Assert.AreEqual("発光", ExifFormat.Flash(0x19));
            Assert.AreEqual("機能なし", ExifFormat.Flash(0x20));
            Assert.IsNull(ExifFormat.Flash(null));
        }

        [TestMethod]
        public void Lookups_Hide_Unknown_Values()
        {
            Assert.AreEqual("絞り優先", ExifFormat.ExposureProgram(3));
            Assert.IsNull(ExifFormat.ExposureProgram(0));
            Assert.AreEqual("分割測光", ExifFormat.MeteringMode(5));
            Assert.IsNull(ExifFormat.MeteringMode(42));
            Assert.AreEqual("オート", ExifFormat.WhiteBalance(0));
            Assert.AreEqual("右90°回転", ExifFormat.Orientation(6));
            Assert.IsNull(ExifFormat.Orientation(9));
            Assert.AreEqual("ISO 400", ExifFormat.Iso(400));
            Assert.IsNull(ExifFormat.Iso(0));
        }

        [TestMethod]
        public void DateTime()
        {
            Assert.AreEqual("2024/05/01 12:34:56", ExifFormat.DateTime("2024:05:01 12:34:56"));
            Assert.AreEqual("2024/05/01 12:34:56（+09:00）", ExifFormat.DateTime("2024:05:01 12:34:56", "+09:00"));
            Assert.IsNull(ExifFormat.DateTime("0000:00:00 00:00:00"), "Unknown date written as zeros");
            Assert.IsNull(ExifFormat.DateTime("    :  :     :  :  "));
            Assert.IsNull(ExifFormat.DateTime(null));
            Assert.AreEqual("2024-05-01", ExifFormat.DateTime("2024-05-01"), "Unreadable dates are shown as they are");
        }

        [TestMethod]
        public void GpsCoordinate()
        {
            var tokyo = new[] { new Rational(35, 1), new Rational(39, 1), new Rational(312, 10) };

            Assert.AreEqual("35°39'31.2\"N（35.658667）", ExifFormat.GpsCoordinate(tokyo, "N"));
            Assert.AreEqual("35°39'31.2\"S（-35.658667）", ExifFormat.GpsCoordinate(tokyo, "S"));

            // Minutes with a fraction and zero seconds
            var decimalMinutes = new[] { new Rational(35, 1), new Rational(3952, 100), new Rational(0, 1) };
            Assert.AreEqual("35°39'31.2\"N（35.658667）", ExifFormat.GpsCoordinate(decimalMinutes, "N"));

            var west = new[] { new Rational(139, 1), new Rational(44, 1), new Rational(0, 1) };
            Assert.AreEqual("139°44'00.0\"W（-139.733333）", ExifFormat.GpsCoordinate(west, "W"));
        }

        [TestMethod]
        public void GpsCoordinate_Rejects_Broken_Values()
        {
            Assert.IsNull(ExifFormat.GpsCoordinate(null, "N"));
            Assert.IsNull(ExifFormat.GpsCoordinate([new Rational(35, 1), new Rational(39, 1)], "N"), "Needs three values");
            Assert.IsNull(ExifFormat.GpsCoordinate([new Rational(35, 0), new Rational(39, 1), new Rational(0, 1)], "N"));
            Assert.IsNull(ExifFormat.GpsCoordinate([new Rational(500, 1), new Rational(0, 1), new Rational(0, 1)], "N"));
        }

        [TestMethod]
        public void Altitude()
        {
            Assert.AreEqual("40.1 m", ExifFormat.Altitude(new Rational(401, 10), 0));
            Assert.AreEqual("-40.1 m", ExifFormat.Altitude(new Rational(401, 10), 1), "Below sea level");
            Assert.AreEqual("12 m", ExifFormat.Altitude(new Rational(12, 1), null));
            Assert.IsNull(ExifFormat.Altitude(new Rational(1, 0), 0));
        }

        [TestMethod]
        public void FileSize()
        {
            Assert.AreEqual("512 バイト", ExifFormat.FileSize(512));
            Assert.AreEqual("1 KB（1,024 バイト）", ExifFormat.FileSize(1024));
            Assert.AreEqual("3.2 MB（3,354,112 バイト）", ExifFormat.FileSize(3_354_112));
        }
    }
}
