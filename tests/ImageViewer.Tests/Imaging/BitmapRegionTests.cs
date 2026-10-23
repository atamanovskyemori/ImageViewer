using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class BitmapRegionTests
    {
        /// <summary>Each pixel's blue = x, green = y (so a pixel tells where it came from)</summary>
        private static byte[] Png(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    pixels[o] = (byte)x;
                    pixels[o + 1] = (byte)y;
                    pixels[o + 3] = 255;
                }
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        [TestMethod]
        public void Copies_The_Region()
        {
            var source = new WicImageDecoder().Decode(Png(100, 80), 0).Image;

            var region = BitmapRegion.Copy(source, new Int32Rect(10, 20, 30, 40));

            Assert.AreEqual(30, region.PixelWidth);
            Assert.AreEqual(40, region.PixelHeight);
            Assert.IsTrue(region.IsFrozen);
            var pixel = new byte[4];
            region.CopyPixels(new Int32Rect(5, 7, 1, 1), pixel, 4, 0);
            Assert.AreEqual(15, pixel[0], "x");
            Assert.AreEqual(27, pixel[1], "y");
        }

        [TestMethod]
        public void Works_On_An_Image_Decoded_On_Another_Thread()
        {
            // The full-size image is decoded on a background thread; a CroppedBitmap of it could not be
            // frozen on the UI thread (it touches the decoder of the other thread). A copy can.
            var data = Png(100, 80);
            var source = Task.Run(() => new WicImageDecoder().Decode(data, 0).Image).Result;

            var region = BitmapRegion.Copy(source, new Int32Rect(50, 40, 20, 10));

            Assert.AreEqual(20, region.PixelWidth);
            var pixel = new byte[4];
            region.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.AreEqual(50, pixel[0]);
            Assert.AreEqual(40, pixel[1]);
        }

        [TestMethod]
        public void Keeps_The_Pixel_Format()
        {
            var gray = BitmapSource.Create(10, 10, 96, 96, PixelFormats.Gray8, null, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(), 10);
            gray.Freeze();

            var region = BitmapRegion.Copy(gray, new Int32Rect(3, 4, 5, 2));

            Assert.AreEqual(PixelFormats.Gray8, region.Format);
            var pixel = new byte[1];
            region.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 1, 0);
            Assert.AreEqual(43, pixel[0]);
        }
    }
}
