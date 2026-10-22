using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    /// <summary>
    /// libwebp fallback (used when Windows has no WebP extension). The bundled libwebp.dll also has the encoder,
    /// so real WebP data is made here. Scaled decoding goes through WebPDecoderConfig, whose C layout is
    /// declared by hand; wrong offsets would show up here as a failure or wrong pixels.
    /// </summary>
    [TestClass]
    public class WebPImageDecoderTests
    {
        [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
        private static extern nuint WebPEncodeLosslessBGRA(byte[] bgra, int width, int height, int stride, out IntPtr output);

        [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPFree(IntPtr ptr);

        private static readonly WebPImageDecoder Decoder = new();

        /// <summary>Left half red, right half blue (opaque BGRA)</summary>
        private static byte[] SourcePixels(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    bool left = x < width / 2;
                    pixels[o] = left ? (byte)0 : (byte)255;     // B
                    pixels[o + 1] = 0;                           // G
                    pixels[o + 2] = left ? (byte)255 : (byte)0;  // R
                    pixels[o + 3] = 255;                         // A
                }
            }
            return pixels;
        }

        private static byte[] EncodeWebP(int width, int height)
        {
            nuint size = WebPEncodeLosslessBGRA(SourcePixels(width, height), width, height, width * 4, out var output);
            Assert.AreNotEqual((nuint)0, size, "encoding failed");
            try
            {
                var webp = new byte[(int)size];
                Marshal.Copy(output, webp, 0, webp.Length);
                return webp;
            }
            finally
            {
                WebPFree(output);
            }
        }

        private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource image, int x, int y)
        {
            var bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[0], pixel[1], pixel[2], pixel[3]);
        }

        [TestInitialize]
        public void SkipWithoutLibWebP()
        {
            if (!Decoder.IsAvailable)
            {
                Assert.Inconclusive("libwebp.dll is not available");
            }
        }

        [TestMethod]
        public void Decoder_Config_Has_The_C_Layout_So_Scaled_Decoding_Is_Used()
        {
            // If this were not 240 (sizeof(WebPDecoderConfig) on x64 in decode.h), the decoder would quietly
            // fall back to "decode full size, then shrink", and the tests below would pass without testing scaling
            var assembly = typeof(WebPImageDecoder).Assembly;
            Assert.AreEqual(240, Marshal.SizeOf(assembly.GetType("ImageViewer.Interop.LibWebP+WebPDecoderConfig", throwOnError: true)!));
            Assert.AreEqual(40, Marshal.SizeOf(assembly.GetType("ImageViewer.Interop.LibWebP+WebPBitstreamFeatures", throwOnError: true)!));
            Assert.AreEqual(120, Marshal.SizeOf(assembly.GetType("ImageViewer.Interop.LibWebP+WebPDecBuffer", throwOnError: true)!));
            Assert.AreEqual(76, Marshal.SizeOf(assembly.GetType("ImageViewer.Interop.LibWebP+WebPDecoderOptions", throwOnError: true)!));
        }

        [TestMethod]
        public void Large_WebP_Is_Read_Reduced_With_Original_Size()
        {
            var image = Decoder.Decode(EncodeWebP(400, 200), maxPixelSize: 100);

            Assert.AreEqual(100, image.Image.PixelWidth);
            Assert.AreEqual(50, image.Image.PixelHeight);
            Assert.AreEqual(400, image.PixelWidth);
            Assert.AreEqual(200, image.PixelHeight);
            Assert.IsTrue(image.IsReduced);
            Assert.IsTrue(image.Image.IsFrozen);
        }

        [TestMethod]
        public void Reduced_WebP_Has_The_Right_Pixels()
        {
            var image = Decoder.Decode(EncodeWebP(400, 200), maxPixelSize: 100).Image;

            var left = PixelAt(image, 10, 25);
            var right = PixelAt(image, 90, 25);
            Assert.IsTrue(left.R > 200 && left.B < 50 && left.A == 255, $"left {left}");
            Assert.IsTrue(right.B > 200 && right.R < 50 && right.A == 255, $"right {right}");
            var corner = PixelAt(image, 99, 49);
            Assert.IsTrue(corner.B > 200 && corner.A == 255, $"bottom-right {corner}");
        }

        [TestMethod]
        public void No_Limit_Reads_Full_Size_Exactly()
        {
            var image = Decoder.Decode(EncodeWebP(400, 200), maxPixelSize: 0).Image;

            Assert.AreEqual(400, image.PixelWidth);
            var pixels = new byte[400 * 200 * 4];
            image.CopyPixels(pixels, 400 * 4, 0);
            CollectionAssert.AreEqual(SourcePixels(400, 200), pixels, "lossless");
        }

        [TestMethod]
        public void Slightly_Larger_WebP_Is_Not_Reduced()
        {
            var image = Decoder.Decode(EncodeWebP(400, 200), maxPixelSize: 350);

            Assert.AreEqual(400, image.Image.PixelWidth);
            Assert.IsFalse(image.IsReduced);
        }

        [TestMethod]
        public void Tall_WebP_Is_Reduced_By_Its_Long_Side()
        {
            var image = Decoder.Decode(EncodeWebP(200, 400), maxPixelSize: 100);

            Assert.AreEqual(50, image.Image.PixelWidth);
            Assert.AreEqual(100, image.Image.PixelHeight);
        }

        [TestMethod]
        public void Thumbnail_Size_Is_Decoded_Directly()
        {
            var bitmap = WebPImageDecoder.DecodeBitmap(EncodeWebP(400, 200), maxPixelSize: 64);

            Assert.AreEqual(64, bitmap.PixelWidth);
            Assert.AreEqual(32, bitmap.PixelHeight);
        }

        [TestMethod]
        public void Small_WebP_Is_Not_Enlarged_For_Thumbnails()
        {
            var bitmap = WebPImageDecoder.DecodeBitmap(EncodeWebP(40, 20), maxPixelSize: 256);

            Assert.AreEqual(40, bitmap.PixelWidth);
            Assert.AreEqual(20, bitmap.PixelHeight);
        }

        [TestMethod]
        public void Broken_WebP_Throws_A_Readable_Error()
        {
            var data = EncodeWebP(400, 200);
            var broken = data[..40]; // header only

            Assert.ThrowsExactly<NotSupportedException>(() => Decoder.Decode(broken, maxPixelSize: 100));
        }
    }
}
