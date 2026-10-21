using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class WicImageDecoderTests
    {
        private static BitmapSource CreateBitmap(int width, int height)
        {
            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3);
            bitmap.Freeze();
            return bitmap;
        }

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap, BitmapMetadata? metadata = null)
        {
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static byte[] Jpeg(int width, int height, ushort? orientation = null)
        {
            BitmapMetadata? metadata = null;
            if (orientation is { } value)
            {
                metadata = new BitmapMetadata("jpg");
                metadata.SetQuery("/app1/ifd/{ushort=274}", value);
            }
            return Encode(new JpegBitmapEncoder(), CreateBitmap(width, height), metadata);
        }

        private static readonly WicImageDecoder Decoder = new();

        [TestMethod]
        public void Large_Image_Is_Read_Reduced_With_Original_Size()
        {
            var image = Decoder.Decode(Jpeg(400, 200), maxPixelSize: 100);

            Assert.AreEqual(100, image.Image.PixelWidth);
            Assert.AreEqual(50, image.Image.PixelHeight);
            Assert.AreEqual(400, image.PixelWidth);
            Assert.AreEqual(200, image.PixelHeight);
            Assert.IsTrue(image.IsReduced);
            Assert.IsTrue(image.Image.IsFrozen);
        }

        [TestMethod]
        public void Tall_Image_Is_Reduced_By_Its_Long_Side()
        {
            var image = Decoder.Decode(Jpeg(200, 400), maxPixelSize: 100);

            Assert.AreEqual(50, image.Image.PixelWidth);
            Assert.AreEqual(100, image.Image.PixelHeight);
            Assert.AreEqual(200, image.PixelWidth);
            Assert.AreEqual(400, image.PixelHeight);
        }

        [TestMethod]
        public void No_Limit_Reads_Full_Size()
        {
            var image = Decoder.Decode(Jpeg(400, 200), maxPixelSize: 0);

            Assert.AreEqual(400, image.Image.PixelWidth);
            Assert.IsFalse(image.IsReduced);
        }

        [TestMethod]
        public void Slightly_Larger_Image_Is_Not_Reduced()
        {
            // 400 is less than 1.25 times 350, so reducing would save little
            var image = Decoder.Decode(Jpeg(400, 200), maxPixelSize: 350);

            Assert.AreEqual(400, image.Image.PixelWidth);
            Assert.IsFalse(image.IsReduced);
        }

        [TestMethod]
        public void Png_Is_Reduced_Too()
        {
            var image = Decoder.Decode(Encode(new PngBitmapEncoder(), CreateBitmap(400, 200)), maxPixelSize: 100);

            Assert.AreEqual(100, image.Image.PixelWidth);
            Assert.AreEqual(400, image.PixelWidth);
        }

        [TestMethod]
        public void Gif_Is_Never_Reduced()
        {
            var bitmap = new FormatConvertedBitmap(CreateBitmap(400, 200), PixelFormats.Indexed8, BitmapPalettes.WebPalette, 0);
            var image = Decoder.Decode(Encode(new GifBitmapEncoder(), bitmap), maxPixelSize: 100);

            Assert.AreEqual(400, image.Image.PixelWidth);
            Assert.IsFalse(image.IsReduced);
        }

        [TestMethod]
        public void Reduced_Image_Applies_Exif_Orientation_And_Reports_Rotated_Size()
        {
            // Orientation 6 = rotate 90° clockwise, so a 400x200 file is shown as 200x400
            var image = Decoder.Decode(Jpeg(400, 200, orientation: 6), maxPixelSize: 100);

            Assert.AreEqual(50, image.Image.PixelWidth);
            Assert.AreEqual(100, image.Image.PixelHeight);
            Assert.AreEqual(200, image.PixelWidth);
            Assert.AreEqual(400, image.PixelHeight);
        }

        [TestMethod]
        public void Full_Size_And_Reduced_Agree_On_Original_Size_With_Orientation()
        {
            var data = Jpeg(400, 200, orientation: 6);
            var full = Decoder.Decode(data, maxPixelSize: 0);
            var reduced = Decoder.Decode(data, maxPixelSize: 100);

            Assert.AreEqual(full.PixelWidth, reduced.PixelWidth);
            Assert.AreEqual(full.PixelHeight, reduced.PixelHeight);
        }
    }
}
