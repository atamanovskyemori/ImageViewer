using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows.Media.Imaging;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class ImageCacheTests
    {
        private const int DefaultTimeoutMs = 5000;

        // Released after each test so that blocked loads never keep thread-pool threads waiting forever.
        private FakeLoader _loader = null!;

        [TestInitialize]
        public void Initialize() => _loader = new FakeLoader();

        [TestCleanup]
        public void Cleanup() => _loader.ReleaseAll();

        [TestMethod]
        public async Task Prefetch_IsInterrupted_By_GetAsync()
        {
            var cache = new ImageCache(_loader);

            var evtA = _loader.Register("A");
            var evtB = _loader.Register("B");

            // Prefetch A -> occupies gate and blocks inside Load(A)
            cache.Prefetch("A");
            await WaitUntil(() => _loader.GetCount("A") == 1);

            // Prefetch B -> will be queued (waiting for gate), so Load(B) should not be called yet
            cache.Prefetch("B");
            await Task.Delay(50);
            Assert.AreEqual(0, _loader.GetCount("B"), "Prefetch B should be queued and not started yet");

            // Request B for display -> should cancel queued prefetch and start immediate load for B
            var taskB = cache.GetAsync("B");
            await WaitUntil(() => _loader.GetCount("B") == 1);

            // Complete immediate load for B
            evtB.Set();
            await taskB;

            // Release A so it can finish. The canceled prefetch of B must not run afterwards.
            evtA.Set();
            await Task.Delay(200);

            Assert.AreEqual(1, _loader.GetCount("A"), "A should be loaded exactly once");
            Assert.AreEqual(1, _loader.GetCount("B"), "B should be loaded exactly once (prefetch canceled, immediate load happened)");
        }

        [TestMethod]
        public async Task Completed_Prefetch_Is_Reused_By_GetAsync()
        {
            var cache = new ImageCache(_loader);
            _loader.Register("A").Set(); // completes immediately

            cache.Prefetch("A");
            await WaitUntil(() => _loader.GetCount("A") == 1);

            // Showing the prefetched image must not decode it again
            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs));
            await Task.Delay(50);

            Assert.AreEqual(1, _loader.GetCount("A"), "A prefetched result should be reused");
        }

        [TestMethod]
        public async Task Running_Prefetch_Is_Joined_By_GetAsync()
        {
            var cache = new ImageCache(_loader);
            var evtA = _loader.Register("A");

            cache.Prefetch("A");
            await WaitUntil(() => _loader.GetCount("A") == 1);

            // Prefetch of A is in progress -> GetAsync should wait for it rather than start another load
            var taskA = cache.GetAsync("A");
            await Task.Delay(50);
            Assert.IsFalse(taskA.IsCompleted, "GetAsync should wait for the running prefetch");

            evtA.Set();
            await taskA.WaitAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs));

            Assert.AreEqual(1, _loader.GetCount("A"), "A should be loaded exactly once");
        }

        [TestMethod]
        public async Task Faulted_Entry_Is_Reloaded_By_GetAsync()
        {
            var cache = new ImageCache(_loader);
            _loader.Register("A").Set();
            _loader.FailNext("A");

            await Assert.ThrowsExactlyAsync<ImageLoadException>(() => cache.GetAsync("A"));

            // Second request should try again instead of returning the cached failure
            await cache.GetAsync("A").WaitAsync(TimeSpan.FromMilliseconds(DefaultTimeoutMs));

            Assert.AreEqual(2, _loader.GetCount("A"), "A should be loaded again after a failure");
        }

        [TestMethod]
        public async Task Retain_Cancels_Queued_Prefetch()
        {
            var cache = new ImageCache(_loader);

            var evtA = _loader.Register("A");
            _loader.Register("B");

            // Prefetch A -> occupies gate
            cache.Prefetch("A");
            await WaitUntil(() => _loader.GetCount("A") == 1);

            // Prefetch B -> queued
            cache.Prefetch("B");
            await Task.Delay(50);
            Assert.AreEqual(0, _loader.GetCount("B"), "Prefetch B should be queued and not started yet");

            // Retain only A -> B's queued prefetch should be canceled
            cache.Retain(new[] { "A" });

            // Release A
            evtA.Set();

            // Give some time to ensure canceled entry won't start
            await Task.Delay(200);

            Assert.AreEqual(0, _loader.GetCount("B"), "Queued prefetch B should have been canceled by Retain");
        }

        [TestMethod]
        public async Task PrefetchGate_Allows_One_At_A_Time()
        {
            var cache = new ImageCache(_loader);

            var evtA = _loader.Register("A");
            var evtB = _loader.Register("B");
            var evtC = _loader.Register("C");

            // Prefetch A -> occupies gate
            cache.Prefetch("A");
            await WaitUntil(() => _loader.GetCount("A") == 1);

            // Prefetch B and C -> both queued
            cache.Prefetch("B");
            cache.Prefetch("C");
            await Task.Delay(50);
            Assert.AreEqual(0, _loader.GetCount("B"), "B should be queued");
            Assert.AreEqual(0, _loader.GetCount("C"), "C should be queued");

            // Release A -> exactly one of B / C should start (which one comes first is not guaranteed,
            // because the two Task.Run calls may reach the gate in either order)
            evtA.Set();
            await WaitUntil(() => _loader.GetCount("B") + _loader.GetCount("C") == 1);
            await Task.Delay(50);
            Assert.AreEqual(1, _loader.GetCount("B") + _loader.GetCount("C"),
                "Only one of B / C should run while the other waits for the gate");

            bool bFirst = _loader.GetCount("B") == 1;
            var (firstEvent, secondPath, secondEvent) = bFirst ? (evtB, "C", evtC) : (evtC, "B", evtB);

            // Release the first one -> the other should start
            firstEvent.Set();
            await WaitUntil(() => _loader.GetCount(secondPath) == 1);

            // Finish the second one
            secondEvent.Set();

            Assert.AreEqual(1, _loader.GetCount("A"));
            Assert.AreEqual(1, _loader.GetCount("B"));
            Assert.AreEqual(1, _loader.GetCount("C"));
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = DefaultTimeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    Assert.Fail("Timeout waiting for condition");
                }
                await Task.Delay(10);
            }
        }

        // Fake loader that counts how many times Load was called per path and can block per path.
        private sealed class FakeLoader : IImageLoader
        {
            private sealed class Entry
            {
                public ManualResetEventSlim Event { get; } = new(false);
                public int Count;
                public int FailNext;
            }

            private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Registers a path. Load blocks until the returned event is set.</summary>
            public ManualResetEventSlim Register(string path)
            {
                var entry = new Entry();
                _map[path] = entry;
                return entry.Event;
            }

            /// <summary>Makes the next Load of the path throw ImageLoadException.</summary>
            public void FailNext(string path) => Volatile.Write(ref _map[path].FailNext, 1);

            public int GetCount(string path) =>
                _map.TryGetValue(path, out var e) ? Volatile.Read(ref e.Count) : 0;

            public void ReleaseAll()
            {
                foreach (var entry in _map.Values)
                {
                    entry.Event.Set();
                }
            }

            public BitmapSource Load(string path)
            {
                if (!_map.TryGetValue(path, out var e))
                {
                    // If not registered, behave as quick success
                    return null!;
                }

                Interlocked.Increment(ref e.Count);

                // Block until signaled to simulate long decode
                e.Event.Wait();

                if (Interlocked.Exchange(ref e.FailNext, 0) != 0)
                {
                    throw new ImageLoadException("fake failure");
                }

                return null!;
            }
        }
    }
}
