using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class StreamingFramesTests
    {
        private const int TimeoutMs = 5000;

        /// <summary>A 1x1 frame whose blue channel is the frame index, so frames can be told apart</summary>
        private static BitmapSource Marker(int index)
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { (byte)index, 0, 0, 255 }, 4);
            bitmap.Freeze();
            return bitmap;
        }

        private static int IndexOf(BitmapSource? frame)
        {
            Assert.IsNotNull(frame);
            var pixel = new byte[4];
            frame.CopyPixels(pixel, 4, 0);
            return pixel[0];
        }

        private static void WaitUntil(Func<bool> condition)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > TimeoutMs)
                {
                    Assert.Fail("Timeout waiting for condition");
                }
                Thread.Sleep(5);
            }
        }

        /// <summary>Records what was produced, in order. Can be told to fail at a frame.</summary>
        private sealed class FakeFrames(int count, int window, int failAt = -1)
            : StreamingFrames(Enumerable.Repeat(TimeSpan.FromMilliseconds(100), count).ToList(), window)
        {
            private readonly List<int> _produced = [];
            private int _resets;

            public int[] Produced
            {
                get { lock (_produced) return _produced.ToArray(); }
            }

            public int Resets => Volatile.Read(ref _resets);

            protected override void Reset() => Interlocked.Increment(ref _resets);

            protected override BitmapSource Produce(int index)
            {
                if (index == failAt)
                {
                    throw new InvalidDataException("broken frame");
                }
                lock (_produced)
                {
                    _produced.Add(index);
                }
                return Marker(index);
            }
        }

        [TestMethod]
        public void Frames_Are_Made_In_Order_From_The_Start()
        {
            var frames = new FakeFrames(count: 20, window: 4);

            frames.Prepare(0);
            WaitUntil(() => frames.Produced.Length >= 3);

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, frames.Produced.Take(3).ToArray());
            Assert.AreEqual(1, frames.Resets);
            Assert.AreEqual(0, IndexOf(frames.Get(0)));
        }

        [TestMethod]
        public void Only_A_Few_Frames_Ahead_Are_Made()
        {
            var frames = new FakeFrames(count: 50, window: 8);

            frames.Prepare(0);
            WaitUntil(() => frames.Produced.Length >= 6);
            Thread.Sleep(100); // give it time to (wrongly) go on

            Assert.IsTrue(frames.Produced.Length <= 8, $"made {frames.Produced.Length} frames");
            Assert.IsTrue(frames.BufferedCount <= 8);
        }

        [TestMethod]
        public void A_Frame_Not_Ready_Yet_Shows_The_Nearest_Earlier_One()
        {
            var frames = new FakeFrames(count: 50, window: 4);
            frames.Prepare(0);
            WaitUntil(() => frames.Produced.Length >= 3);
            Thread.Sleep(50);

            int lastMade = frames.Produced.Max();
            Assert.AreEqual(lastMade, IndexOf(frames.Get(lastMade + 10)));
        }

        [TestMethod]
        public void Moving_Forward_Drops_Old_Frames()
        {
            var frames = new FakeFrames(count: 50, window: 4);
            frames.Prepare(0);
            WaitUntil(() => frames.Produced.Length >= 3);

            frames.Prepare(30);
            WaitUntil(() => frames.Get(32) is { } f && IndexOf(f) == 32);

            Assert.IsTrue(frames.BufferedCount <= 5, $"keeps {frames.BufferedCount} frames");
            Assert.AreEqual(1, frames.Resets, "went forward without starting over");
        }

        [TestMethod]
        public void While_Catching_Up_The_Latest_Made_Frame_Is_Shown()
        {
            // Frames far ahead are made one by one; there is always something to show on the way
            var frames = new FakeFrames(count: 250, window: 4); // the marker holds the index in one byte
            frames.Prepare(0);
            WaitUntil(() => frames.Produced.Length >= 3);

            frames.Prepare(249);
            WaitUntil(() =>
            {
                var frame = frames.Get(249);
                Assert.IsNotNull(frame, "nothing to show while catching up");
                return IndexOf(frame) == 249;
            });
        }

        [TestMethod]
        public void Going_Back_To_A_Dropped_Frame_Starts_Over()
        {
            var frames = new FakeFrames(count: 50, window: 4);
            frames.Prepare(30);
            WaitUntil(() => frames.Get(30) is { } f && IndexOf(f) == 30);

            frames.Prepare(2);
            WaitUntil(() => frames.Get(2) is { } f && IndexOf(f) == 2);

            Assert.AreEqual(2, frames.Resets);
        }

        [TestMethod]
        public void Going_Back_One_Frame_Uses_The_Kept_Frame()
        {
            var frames = new FakeFrames(count: 50, window: 8); // keeps 2 frames behind
            frames.Prepare(20);
            WaitUntil(() => frames.Get(20) is { } f && IndexOf(f) == 20);
            frames.Prepare(21);
            Thread.Sleep(50);

            frames.Prepare(20);
            Assert.AreEqual(20, IndexOf(frames.Get(20)));
            Assert.AreEqual(1, frames.Resets, "no need to start over");
        }

        [TestMethod]
        public void The_End_Wraps_Around_To_The_First_Frame()
        {
            // Playing through 10 frames one by one: near the end, frame 0 of the next lap is made again
            var frames = new FakeFrames(count: 10, window: 4);
            for (int i = 0; i < 10; i++)
            {
                frames.Prepare(i);
                int index = i;
                WaitUntil(() => frames.Get(index) is { } f && IndexOf(f) == index);
            }
            WaitUntil(() => frames.Resets == 2);
            frames.Prepare(0);
            WaitUntil(() => frames.Get(0) is { } f && IndexOf(f) == 0);

            var produced = frames.Produced;
            int nine = Array.IndexOf(produced, 9);
            CollectionAssert.AreEqual(new[] { 9, 0 }, produced[nine..(nine + 2)], string.Join(",", produced));
        }

        [TestMethod]
        public void A_Broken_Frame_Stops_Making_Frames_Without_Hanging()
        {
            var frames = new FakeFrames(count: 10, window: 6, failAt: 2);
            frames.Prepare(0);
            WaitUntil(() => frames.Produced.Length >= 2);
            Thread.Sleep(100);

            Assert.AreEqual(1, IndexOf(frames.Get(5)), "shows the last frame it could make");

            frames.Prepare(0); // asking again just tries again
            Thread.Sleep(100);
            Assert.AreEqual(1, IndexOf(frames.Get(5)));
        }

        [TestMethod]
        [DataRow(1_000_000L, 256_000_000L, 16)]
        [DataRow(6_000_000L, 256_000_000L, 10)]
        [DataRow(100_000_000L, 256_000_000L, 3)]
        public void Window_Fits_In_A_Quarter_Of_The_Limit_Between_3_And_16(long frameBytes, long maxBytes, int expected)
        {
            Assert.AreEqual(expected, AnimationLimits.StreamingWindow(frameBytes, maxBytes));
        }

        // ------------------------------------------------------------
        // Real GIF decoding
        // ------------------------------------------------------------

        private static byte[] AnimatedGif(params Color[] colors)
        {
            var encoder = new GifBitmapEncoder();
            foreach (var color in colors)
            {
                var pixels = new byte[40 * 30 * 3];
                for (int i = 0; i < pixels.Length; i += 3)
                {
                    pixels[i] = color.R;
                    pixels[i + 1] = color.G;
                    pixels[i + 2] = color.B;
                }
                var rgb = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Rgb24, null, pixels, 40 * 3);
                encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(rgb, PixelFormats.Indexed8, BitmapPalettes.WebPalette, 0)));
            }
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static byte[] PixelsOf(BitmapSource frame)
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return pixels;
        }

        [TestMethod]
        public void Gif_Over_The_Limit_Plays_From_Frames_Made_On_The_Fly()
        {
            var gif = AnimatedGif(Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow);
            long oneCanvas = 40 * 30 * 4;

            var full = new WicImageDecoder(maxAnimationBytes: long.MaxValue).Decode(gif, 0);
            var streamed = new WicImageDecoder(maxAnimationBytes: oneCanvas * 2).Decode(gif, 0); // all 4 do not fit

            Assert.IsNotNull(full.Frames, "the normal way keeps every frame");
            Assert.IsNull(streamed.Frames);
            Assert.IsInstanceOfType<StreamingFrames>(streamed.Animation);
            Assert.IsTrue(streamed.IsAnimated);
            StringAssert.Contains(streamed.Notice, "省メモリ");
            CollectionAssert.AreEqual(full.Animation!.Delays.ToArray(), streamed.Animation!.Delays.ToArray());

            for (int i = 0; i < 4; i++)
            {
                var expected = PixelsOf(full.Frames[i].Image);
                streamed.Animation.Prepare(i);
                int index = i;
                WaitUntil(() => streamed.Animation.Get(index) is { } f && PixelsOf(f).SequenceEqual(expected));
            }
        }

        [TestMethod]
        public void Gif_Too_Big_For_Even_One_Canvas_Shows_Only_The_First_Frame()
        {
            var gif = AnimatedGif(Colors.Red, Colors.Lime);

            var image = new WicImageDecoder(maxAnimationBytes: 100).Decode(gif, 0);

            Assert.IsFalse(image.IsAnimated);
            StringAssert.Contains(image.Notice, "アニメ省略");
        }
    }
}
