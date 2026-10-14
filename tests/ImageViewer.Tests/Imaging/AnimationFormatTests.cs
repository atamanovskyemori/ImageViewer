using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    /// <summary>Format-level helpers: GIF loop count / delay, WebP animation detection, DecodedImage.</summary>
    [TestClass]
    public class AnimationFormatTests
    {
        private static readonly byte[] Netscape = Encoding.ASCII.GetBytes("NETSCAPE2.0");

        [TestMethod]
        public void Gif_Without_Loop_Extension_Plays_Once()
        {
            Assert.AreEqual(1, GifAnimation.ParseLoopCount(null, null));
        }

        [TestMethod]
        public void Gif_Loop_Zero_Means_Infinite()
        {
            Assert.AreEqual(0, GifAnimation.ParseLoopCount(Netscape, new byte[] { 3, 1, 0, 0, 0 }));
        }

        [TestMethod]
        public void Gif_Loop_N_Means_N_Plus_One_Plays()
        {
            Assert.AreEqual(6, GifAnimation.ParseLoopCount(Netscape, new byte[] { 3, 1, 5, 0, 0 }));
            Assert.AreEqual(257, GifAnimation.ParseLoopCount(Netscape, new byte[] { 3, 1, 0, 1, 0 }), "Little endian");
        }

        [TestMethod]
        public void Gif_Loop_Accepts_AnimExts()
        {
            var animExts = Encoding.ASCII.GetBytes("ANIMEXTS1.0");
            Assert.AreEqual(0, GifAnimation.ParseLoopCount(animExts, new byte[] { 3, 1, 0, 0, 0 }));
        }

        [TestMethod]
        public void Gif_Loop_Broken_Data_Plays_Once()
        {
            Assert.AreEqual(1, GifAnimation.ParseLoopCount(Encoding.ASCII.GetBytes("XMP DataXMP"), new byte[] { 3, 1, 0, 0, 0 }));
            Assert.AreEqual(1, GifAnimation.ParseLoopCount(Netscape, new byte[] { 3, 1 }));
            Assert.AreEqual(1, GifAnimation.ParseLoopCount(Netscape, new byte[] { 3, 2, 0, 0, 0 }));
        }

        [TestMethod]
        public void Gif_Delay_Is_In_Centiseconds()
        {
            Assert.AreEqual(TimeSpan.FromMilliseconds(50), GifAnimation.DelayFromCentiseconds(5));
            Assert.AreEqual(TimeSpan.Zero, GifAnimation.DelayFromCentiseconds(-1));
        }

        [TestMethod]
        public void Too_Short_Delay_Becomes_100ms()
        {
            Assert.AreEqual(TimeSpan.FromMilliseconds(100), AnimationLimits.NormalizeDelay(TimeSpan.Zero));
            Assert.AreEqual(TimeSpan.FromMilliseconds(100), AnimationLimits.NormalizeDelay(TimeSpan.FromMilliseconds(10)));
            Assert.AreEqual(TimeSpan.FromMilliseconds(20), AnimationLimits.NormalizeDelay(TimeSpan.FromMilliseconds(20)));
        }

        [TestMethod]
        public void EstimateBytes_Uses_4_Bytes_Per_Pixel()
        {
            Assert.AreEqual(500L * 500 * 4 * 100, AnimationLimits.EstimateBytes(500, 500, 100));
        }

        [TestMethod]
        public void WebP_Animation_Flag_Is_Detected()
        {
            Assert.IsTrue(WebPAnimationDecoder.IsAnimatedWebP(WebPHeader("VP8X", 0x02)));
            Assert.IsTrue(WebPAnimationDecoder.IsAnimatedWebP(WebPHeader("VP8X", 0x12)), "Alpha + animation");
        }

        [TestMethod]
        public void Still_WebP_Is_Not_Animated()
        {
            Assert.IsFalse(WebPAnimationDecoder.IsAnimatedWebP(WebPHeader("VP8X", 0x10)), "Alpha only");
            Assert.IsFalse(WebPAnimationDecoder.IsAnimatedWebP(WebPHeader("VP8 ", 0x02)), "Simple lossy format has no flags");
            Assert.IsFalse(WebPAnimationDecoder.IsAnimatedWebP(WebPHeader("VP8L", 0x02)), "Simple lossless format has no flags");
        }

        [TestMethod]
        public void Non_WebP_Or_Short_Header_Is_Not_Animated()
        {
            Assert.IsFalse(WebPAnimationDecoder.IsAnimatedWebP(new byte[10]));

            var gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[30]).ToArray();
            Assert.IsFalse(WebPAnimationDecoder.IsAnimatedWebP(gif));
        }

        [TestMethod]
        public void DecodedImage_With_One_Frame_Is_Still()
        {
            var image = new DecodedImage(new[] { new AnimationFrame(CreateBitmap(10, 10), TimeSpan.FromMilliseconds(100)) }, 0);

            Assert.IsFalse(image.IsAnimated);
            Assert.IsNull(image.Frames);
        }

        [TestMethod]
        public void DecodedImage_With_Frames_Is_Animated_And_Sums_Size()
        {
            var frames = new[]
            {
                new AnimationFrame(CreateBitmap(10, 10), TimeSpan.FromMilliseconds(100)),
                new AnimationFrame(CreateBitmap(10, 10), TimeSpan.FromMilliseconds(100)),
            };

            var image = new DecodedImage(frames, -5);

            Assert.IsTrue(image.IsAnimated);
            Assert.AreSame(frames[0].Image, image.Image, "Image should be the first frame");
            Assert.AreEqual(2 * 10 * 10 * 4L, image.ByteSize);
            Assert.AreEqual(0, image.LoopCount, "Negative loop count should be treated as infinite");
        }

        [TestMethod]
        public void DecodedImage_Without_Frames_Throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new DecodedImage(Array.Empty<AnimationFrame>(), 0));
        }

        /// <summary>"RIFF" size "WEBP" chunk size flags + padding</summary>
        private static byte[] WebPHeader(string chunk, byte flags)
        {
            var header = new byte[30];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            Encoding.ASCII.GetBytes("WEBP").CopyTo(header, 8);
            Encoding.ASCII.GetBytes(chunk).CopyTo(header, 12);
            header[20] = flags;
            return header;
        }

        private static BitmapSource CreateBitmap(int width, int height)
        {
            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
