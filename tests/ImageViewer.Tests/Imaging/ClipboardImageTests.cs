using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class ClipboardImageTests
    {
        private static BitmapSource CreateBitmap(int width, int height)
        {
            // Half-transparent pixels, to check that the PNG keeps alpha
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 3] = 0x80;
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        [TestMethod]
        public void Puts_Both_A_Bitmap_And_A_Png()
        {
            var data = ClipboardImage.CreateDataObject(CreateBitmap(3, 2));

            Assert.IsTrue(data.GetDataPresent(DataFormats.Bitmap));
            Assert.IsTrue(data.GetDataPresent(ClipboardImage.PngFormat));
        }

        [TestMethod]
        public void Png_Keeps_Size_And_Transparency()
        {
            var data = ClipboardImage.CreateDataObject(CreateBitmap(3, 2));
            var stream = (MemoryStream)data.GetData(ClipboardImage.PngFormat)!;

            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Assert.AreEqual(3, decoded.PixelWidth);
            Assert.AreEqual(2, decoded.PixelHeight);

            var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[3 * 2 * 4];
            converted.CopyPixels(pixels, 3 * 4, 0);
            Assert.AreEqual(0x80, pixels[3]);
        }
    }
}
