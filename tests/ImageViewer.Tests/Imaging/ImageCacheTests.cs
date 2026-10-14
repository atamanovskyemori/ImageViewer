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

        [TestMethod]
        public async Task Prefetch_IsInterrupted_By_GetAsync()
        {
            var loader = new FakeLoader();
            var cache = new ImageCache(loader);

            var pathA = "A";
            var pathB = "B";

            var evtA = new ManualResetEventSlim(false);
            var evtB = new ManualResetEventSlim(false);

            loader.Register(pathA, evtA);
            loader.Register(pathB, evtB);

            // Prefetch A -> occupies gate and blocks inside Load(A)
            cache.Prefetch(pathA);
            await WaitUntil(() => loader.GetCount(pathA) == 1);

            // Prefetch B -> will be queued (waiting for gate), so Load(B) should not be called yet
            cache.Prefetch(pathB);
            await Task.Delay(50);
            Assert.AreEqual(0, loader.GetCount(pathB), "Prefetch B should be queued and not started yet");

            // Request B for display -> should cancel queued prefetch and start immediate load for B
            var taskB = cache.GetAsync(pathB);
            await WaitUntil(() => loader.GetCount(pathB) == 1);

            // Complete immediate load for B
            evtB.Set();
            await taskB;

            // Release A so it can finish
            evtA.Set();
            await WaitUntil(() => loader.GetCount(pathA) == 1);

            Assert.AreEqual(1, loader.GetCount(pathA), "A should be loaded exactly once");
            Assert.AreEqual(1, loader.GetCount(pathB), "B should be loaded exactly once (prefetch canceled, immediate load happened)");
        }

        [TestMethod]
        public async Task Retain_Cancels_Queued_Prefetch()
        {
            var loader = new FakeLoader();
            var cache = new ImageCache(loader);

            var pathA = "A";
            var pathB = "B";

            var evtA = new ManualResetEventSlim(false);
            var evtB = new ManualResetEventSlim(false);

            loader.Register(pathA, evtA);
            loader.Register(pathB, evtB);

            // Prefetch A -> occupies gate
            cache.Prefetch(pathA);
            await WaitUntil(() => loader.GetCount(pathA) == 1);

            // Prefetch B -> queued
            cache.Prefetch(pathB);
            await Task.Delay(50);
            Assert.AreEqual(0, loader.GetCount(pathB), "Prefetch B should be queued and not started yet");

            // Retain only A -> B's queued prefetch should be canceled
            cache.Retain(new[] { pathA });

            // Release A
            evtA.Set();

            // Give some time to ensure canceled entry won't start
            await Task.Delay(200);

            Assert.AreEqual(0, loader.GetCount(pathB), "Queued prefetch B should have been canceled by Retain");
        }

        [TestMethod]
        public async Task PrefetchGate_Allows_One_At_A_Time()
        {
            var loader = new FakeLoader();
            var cache = new ImageCache(loader);

            var pathA = "A";
            var pathB = "B";
            var pathC = "C";

            var evtA = new ManualResetEventSlim(false);
            var evtB = new ManualResetEventSlim(false);
            var evtC = new ManualResetEventSlim(false);

            loader.Register(pathA, evtA);
            loader.Register(pathB, evtB);
            loader.Register(pathC, evtC);

            // Prefetch A -> occupies gate
            cache.Prefetch(pathA);
            await WaitUntil(() => loader.GetCount(pathA) == 1);

            // Prefetch B and C -> both queued
            cache.Prefetch(pathB);
            cache.Prefetch(pathC);
            await Task.Delay(50);
            Assert.AreEqual(0, loader.GetCount(pathB), "B should be queued");
            Assert.AreEqual(0, loader.GetCount(pathC), "C should be queued");

            // Release A -> B should start (and block)
            evtA.Set();
            await WaitUntil(() => loader.GetCount(pathB) == 1);

            // C should still be queued until B finishes
            Assert.AreEqual(0, loader.GetCount(pathC), "C should remain queued while B holds the gate");

            // Release B -> C should start
            evtB.Set();
            await WaitUntil(() => loader.GetCount(pathC) == 1);

            // Finish C
            evtC.Set();

            Assert.AreEqual(1, loader.GetCount(pathA));
            Assert.AreEqual(1, loader.GetCount(pathB));
            Assert.AreEqual(1, loader.GetCount(pathC));
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

        // Simple fake loader that tracks how many times Load was called per path and can block per-path.
        private class FakeLoader
        {
            private class Entry
            {
                public ManualResetEventSlim Event = new(false);
                public int Count;
            }

            private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);

            public void Register(string path, ManualResetEventSlim evt)
            {
                _map[path] = new Entry { Event = evt, Count = 0 };
            }

            public int GetCount(string path)
            {
                return _map.TryGetValue(path, out var e) ? Volatile.Read(ref e.Count) : 0;
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
                return null!;
            }
        }
    }
}