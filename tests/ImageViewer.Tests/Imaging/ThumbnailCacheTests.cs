using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class ThumbnailCacheTests
    {
        private const int TimeoutMs = 5000;

        // 10 x 10 Bgra32 = 400 bytes per thumbnail
        private const long BytesPerThumbnail = 10 * 10 * 4;

        private FakeDecoder _decoder = null!;
        private Dictionary<string, FileStamp?> _stamps = null!;

        [TestInitialize]
        public void Initialize()
        {
            _decoder = new FakeDecoder();
            _stamps = new Dictionary<string, FileStamp?>(StringComparer.OrdinalIgnoreCase);
        }

        [TestCleanup]
        public void Cleanup() => _decoder.ReleaseAll();

        private ThumbnailCache CreateCache(long maxBytes = 1024 * 1024, int maxConcurrency = 2) =>
            new(_decoder, 256, maxBytes, maxConcurrency, path =>
            {
                lock (_stamps)
                {
                    return _stamps.TryGetValue(path, out var stamp) ? stamp : new FileStamp(DateTime.UnixEpoch, 1);
                }
            });

        private void SetStamp(string path, long length)
        {
            lock (_stamps)
            {
                _stamps[path] = new FileStamp(DateTime.UnixEpoch, length);
            }
        }

        [TestMethod]
        public async Task Second_Request_Is_Served_From_Cache_Synchronously()
        {
            var cache = CreateCache();

            var first = await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var second = cache.GetAsync("A");

            Assert.IsTrue(second.IsCompletedSuccessfully, "Cached thumbnail should be returned without waiting");
            Assert.AreSame(first.Image, second.Result.Image);
            Assert.AreEqual(1, _decoder.GetCount("A"));
        }

        [TestMethod]
        public async Task Decoder_Gets_The_Pixel_Size()
        {
            var cache = CreateCache();

            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.AreEqual(256, _decoder.LastMaxPixelSize);
        }

        [TestMethod]
        public async Task Failure_Is_Returned_And_Remembered()
        {
            var cache = CreateCache();
            _decoder.FailAlways("Broken");

            var result = await cache.GetAsync("Broken").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var again = await cache.GetAsync("Broken").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.IsTrue(result.IsFailed);
            Assert.IsTrue(again.IsFailed);
            Assert.AreEqual(1, _decoder.GetCount("Broken"), "A failed file should not be decoded again and again");
        }

        [TestMethod]
        public async Task Least_Recently_Used_Is_Evicted_When_Over_The_Limit()
        {
            // room for two thumbnails
            var cache = CreateCache(maxBytes: BytesPerThumbnail * 2);

            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await cache.GetAsync("B").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            _ = cache.GetAsync("A"); // A is now the most recently used
            await cache.GetAsync("C").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.AreEqual(2, cache.Count);
            Assert.AreEqual(BytesPerThumbnail * 2, cache.CurrentBytes);
            Assert.IsTrue(cache.GetAsync("A").IsCompletedSuccessfully, "A was used recently and should stay");

            await cache.GetAsync("B").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.AreEqual(2, _decoder.GetCount("B"), "B should have been evicted and decoded again");
        }

        [TestMethod]
        public async Task Newest_Thumbnail_Is_Kept_Even_If_Larger_Than_The_Limit()
        {
            var cache = CreateCache(maxBytes: 1);

            var result = await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.IsFalse(result.IsFailed);
            Assert.AreEqual(1, cache.Count);
        }

        [TestMethod]
        public async Task Invalidate_Keeps_Thumbnail_When_File_Is_Unchanged()
        {
            var cache = CreateCache();
            SetStamp("A", 100);
            var before = await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            cache.Invalidate();
            var after = await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.AreSame(before.Image, after.Image);
            Assert.AreEqual(1, _decoder.GetCount("A"));

            // checked once; served synchronously again
            Assert.IsTrue(cache.GetAsync("A").IsCompletedSuccessfully);
        }

        [TestMethod]
        public async Task Invalidate_Recreates_Thumbnail_When_File_Changed()
        {
            var cache = CreateCache();
            SetStamp("A", 100);
            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            SetStamp("A", 200);
            cache.Invalidate();
            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.AreEqual(2, _decoder.GetCount("A"));
            Assert.AreEqual(1, cache.Count, "The old thumbnail should be replaced, not added");
            Assert.AreEqual(BytesPerThumbnail, cache.CurrentBytes);
        }

        [TestMethod]
        public async Task Without_Invalidate_Changed_File_Is_Not_Checked()
        {
            var cache = CreateCache();
            SetStamp("A", 100);
            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            SetStamp("A", 200);
            Assert.IsTrue(cache.GetAsync("A").IsCompletedSuccessfully);
            Assert.AreEqual(1, _decoder.GetCount("A"));
        }

        [TestMethod]
        public async Task Concurrency_Is_Limited()
        {
            var cache = CreateCache(maxConcurrency: 2);
            var gateA = _decoder.Block("A");
            var gateB = _decoder.Block("B");
            _decoder.Block("C");

            var a = cache.GetAsync("A");
            var b = cache.GetAsync("B");
            var c = cache.GetAsync("C");

            await WaitUntil(() => _decoder.GetCount("A") == 1 && _decoder.GetCount("B") == 1);
            await Task.Delay(100);
            Assert.AreEqual(0, _decoder.GetCount("C"), "Only two thumbnails should be decoded at once");

            gateA.Set();
            await a.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await WaitUntil(() => _decoder.GetCount("C") == 1);

            gateB.Set();
            _decoder.ReleaseAll();
            await Task.WhenAll(b, c).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        }

        [TestMethod]
        public async Task Request_Canceled_While_Waiting_Is_Not_Decoded()
        {
            var cache = CreateCache(maxConcurrency: 1);
            var gateA = _decoder.Block("A");
            var a = cache.GetAsync("A");
            await WaitUntil(() => _decoder.GetCount("A") == 1);

            using var cts = new CancellationTokenSource();
            var b = cache.GetAsync("B", cts.Token);
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => b);

            gateA.Set();
            await a.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await Task.Delay(100);

            Assert.AreEqual(0, _decoder.GetCount("B"), "Canceled request should never reach the decoder");
        }

        [TestMethod]
        public async Task Clear_Removes_Everything()
        {
            var cache = CreateCache();
            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            cache.Clear();

            Assert.AreEqual(0, cache.Count);
            Assert.AreEqual(0, cache.CurrentBytes);
        }

        [TestMethod]
        public async Task Paths_Are_Case_Insensitive()
        {
            var cache = CreateCache();
            await cache.GetAsync(@"C:\Pics\A.jpg").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.IsTrue(cache.GetAsync(@"c:\pics\a.JPG").IsCompletedSuccessfully);
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Condition was not met in time");
                }
                await Task.Delay(10);
            }
        }

        private sealed class FakeDecoder : IThumbnailDecoder
        {
            private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<string, bool> _failing = new(StringComparer.OrdinalIgnoreCase);

            public int LastMaxPixelSize;

            /// <summary>Decode of the path blocks until the returned event is set.</summary>
            public ManualResetEventSlim Block(string path) => _gates.GetOrAdd(path, _ => new ManualResetEventSlim(false));

            public void FailAlways(string path) => _failing[path] = true;

            public int GetCount(string path) => _counts.TryGetValue(path, out int count) ? count : 0;

            public void ReleaseAll()
            {
                foreach (var gate in _gates.Values)
                {
                    gate.Set();
                }
            }

            public BitmapSource Decode(string path, int maxPixelSize)
            {
                Volatile.Write(ref LastMaxPixelSize, maxPixelSize);
                _counts.AddOrUpdate(path, 1, (_, count) => count + 1);

                if (_gates.TryGetValue(path, out var gate))
                {
                    gate.Wait(TimeoutMs);
                }

                if (_failing.ContainsKey(path))
                {
                    throw new NotSupportedException("fake failure");
                }

                var bitmap = BitmapSource.Create(10, 10, 96, 96, PixelFormats.Bgra32, null, new byte[10 * 10 * 4], 10 * 4);
                bitmap.Freeze();
                return bitmap;
            }
        }
    }
}
