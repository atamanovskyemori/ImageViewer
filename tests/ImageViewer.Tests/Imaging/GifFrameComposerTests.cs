using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class GifFrameComposerTests
    {
        private static readonly (byte B, byte G, byte R, byte A) Clear = (0, 0, 0, 0);
        private static readonly (byte B, byte G, byte R, byte A) Red = (0, 0, 255, 255);
        private static readonly (byte B, byte G, byte R, byte A) Blue = (255, 0, 0, 255);
        private static readonly (byte B, byte G, byte R, byte A) Green = (0, 255, 0, 255);

        [TestMethod]
        public void First_Frame_Is_Drawn_At_Its_Offset_On_Transparent_Canvas()
        {
            var composer = new GifFrameComposer(4, 4);

            var canvas = composer.Compose(Solid(2, 2, Red), left: 1, top: 1, width: 2, height: 2, GifDisposal.None);

            Assert.AreEqual(Clear, Pixel(canvas, 4, 0, 0));
            Assert.AreEqual(Red, Pixel(canvas, 4, 1, 1));
            Assert.AreEqual(Red, Pixel(canvas, 4, 2, 2));
            Assert.AreEqual(Clear, Pixel(canvas, 4, 3, 3));
        }

        [TestMethod]
        public void Transparent_Pixels_Keep_What_Is_Below()
        {
            var composer = new GifFrameComposer(2, 1);
            composer.Compose(Solid(2, 1, Red), 0, 0, 2, 1, GifDisposal.DoNotDispose);

            // Second frame: left pixel transparent, right pixel blue
            var frame = new byte[] { 0, 0, 0, 0, /* */ 255, 0, 0, 255 };
            var canvas = composer.Compose(frame, 0, 0, 2, 1, GifDisposal.None);

            Assert.AreEqual(Red, Pixel(canvas, 2, 0, 0));
            Assert.AreEqual(Blue, Pixel(canvas, 2, 1, 0));
        }

        [TestMethod]
        public void DoNotDispose_Keeps_Previous_Frame_Under_Next()
        {
            var composer = new GifFrameComposer(2, 1);
            composer.Compose(Solid(2, 1, Red), 0, 0, 2, 1, GifDisposal.DoNotDispose);

            var canvas = composer.Compose(Solid(1, 1, Blue), 1, 0, 1, 1, GifDisposal.None);

            Assert.AreEqual(Red, Pixel(canvas, 2, 0, 0));
            Assert.AreEqual(Blue, Pixel(canvas, 2, 1, 0));
        }

        [TestMethod]
        public void RestoreBackground_Clears_Only_The_Frame_Area_To_Transparent()
        {
            var composer = new GifFrameComposer(2, 1);
            composer.Compose(Solid(2, 1, Red), 0, 0, 2, 1, GifDisposal.DoNotDispose);

            // Blue on the right, then restore background -> right becomes transparent, left stays red
            var shown = composer.Compose(Solid(1, 1, Blue), 1, 0, 1, 1, GifDisposal.RestoreBackground);
            Assert.AreEqual(Blue, Pixel(shown, 2, 1, 0), "The frame itself is shown before disposal");

            var next = composer.Compose(Solid(1, 1, Clear), 0, 0, 1, 1, GifDisposal.None);
            Assert.AreEqual(Red, Pixel(next, 2, 0, 0));
            Assert.AreEqual(Clear, Pixel(next, 2, 1, 0));
        }

        [TestMethod]
        public void RestorePrevious_Returns_To_State_Before_The_Frame()
        {
            var composer = new GifFrameComposer(2, 1);
            composer.Compose(Solid(2, 1, Red), 0, 0, 2, 1, GifDisposal.DoNotDispose);

            var shown = composer.Compose(Solid(2, 1, Blue), 0, 0, 2, 1, GifDisposal.RestorePrevious);
            Assert.AreEqual(Blue, Pixel(shown, 2, 0, 0));

            var next = composer.Compose(Solid(1, 1, Green), 1, 0, 1, 1, GifDisposal.None);
            Assert.AreEqual(Red, Pixel(next, 2, 0, 0), "Blue frame should have been undone");
            Assert.AreEqual(Green, Pixel(next, 2, 1, 0));
        }

        [TestMethod]
        public void Frame_Outside_Canvas_Is_Clipped()
        {
            var composer = new GifFrameComposer(2, 2);

            // 3x3 frame starting at (-1, -1): only its bottom-right 2x2 lands on the canvas
            var canvas = composer.Compose(Solid(3, 3, Red), -1, -1, 3, 3, GifDisposal.RestoreBackground);

            Assert.AreEqual(Red, Pixel(canvas, 2, 0, 0));
            Assert.AreEqual(Red, Pixel(canvas, 2, 1, 1));

            // Completely outside: nothing happens, no exception
            var after = composer.Compose(Solid(1, 1, Blue), 10, 10, 1, 1, GifDisposal.RestoreBackground);
            Assert.AreEqual(Clear, Pixel(after, 2, 0, 0), "Previous frame was disposed to background");
        }

        [TestMethod]
        public void Returned_Snapshot_Is_A_Copy()
        {
            var composer = new GifFrameComposer(1, 1);
            var first = composer.Compose(Solid(1, 1, Red), 0, 0, 1, 1, GifDisposal.DoNotDispose);

            first[0] = 123; // must not affect the composer
            var second = composer.Compose(Solid(1, 1, Clear), 0, 0, 1, 1, GifDisposal.None);

            Assert.AreEqual(Red, Pixel(second, 1, 0, 0));
        }

        [TestMethod]
        public void Too_Few_Pixels_Throws()
        {
            var composer = new GifFrameComposer(2, 2);

            Assert.ThrowsExactly<ArgumentException>(() =>
                composer.Compose(new byte[4], 0, 0, 2, 2, GifDisposal.None));
        }

        private static byte[] Solid(int width, int height, (byte B, byte G, byte R, byte A) color)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = color.A;
            }
            return pixels;
        }

        private static (byte B, byte G, byte R, byte A) Pixel(byte[] canvas, int canvasWidth, int x, int y)
        {
            int i = ((y * canvasWidth) + x) * 4;
            return (canvas[i], canvas[i + 1], canvas[i + 2], canvas[i + 3]);
        }
    }
}
