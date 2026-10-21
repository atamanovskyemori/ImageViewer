using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests
{
    [TestClass]
    public class SingleInstanceTests
    {
        // A name nobody else uses, so tests never talk to a real running viewer
        private static string UniqueName() => "ImageViewer-Test-" + Guid.NewGuid().ToString("N");

        [TestMethod]
        public void Name_Is_The_Same_For_The_Same_Folder()
        {
            Assert.AreEqual(
                SingleInstance.NameFor(@"C:\Apps\ImageViewer"),
                SingleInstance.NameFor(@"c:\apps\imageviewer\"));
        }

        [TestMethod]
        public void Name_Differs_For_Different_Folders()
        {
            Assert.AreNotEqual(
                SingleInstance.NameFor(@"C:\Apps\ImageViewer"),
                SingleInstance.NameFor(@"E:\Projects\ImageViewer\bin\Debug"));
        }

        [TestMethod]
        public void Only_The_First_Launch_Acquires()
        {
            var name = UniqueName();
            using var first = SingleInstance.TryAcquire(name);
            Assert.IsNotNull(first);

            // Another launch runs on a different thread (like a different process); the mutex is already taken
            var second = Task.Run(() => SingleInstance.TryAcquire(name)).Result;
            Assert.IsNull(second);
        }

        [TestMethod]
        public void Can_Acquire_Again_After_The_First_Exits()
        {
            var name = UniqueName();
            SingleInstance.TryAcquire(name)!.Dispose();

            var again = Task.Run(() =>
            {
                using var instance = SingleInstance.TryAcquire(name);
                return instance is not null;
            }).Result;
            Assert.IsTrue(again);
        }

        [TestMethod]
        public async Task Path_Is_Delivered_To_The_First_Launch()
        {
            var name = UniqueName();
            using var first = SingleInstance.TryAcquire(name)!;
            var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.StartListening(path => received.TrySetResult(path));

            const string path = @"C:\写真\2026\IMG_0001.jpg";
            Assert.IsTrue(await Task.Run(() => SingleInstance.TrySend(name, path)));

            Assert.AreEqual(path, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [TestMethod]
        public async Task Launch_Without_Path_Only_Brings_Window_Forward()
        {
            var name = UniqueName();
            using var first = SingleInstance.TryAcquire(name)!;
            var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.StartListening(path => received.TrySetResult(path));

            Assert.IsTrue(await Task.Run(() => SingleInstance.TrySend(name, null)));

            Assert.IsNull(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [TestMethod]
        public async Task Several_Launches_Are_All_Delivered()
        {
            var name = UniqueName();
            using var first = SingleInstance.TryAcquire(name)!;
            var paths = new System.Collections.Concurrent.ConcurrentQueue<string?>();
            first.StartListening(paths.Enqueue);

            for (int i = 0; i < 3; i++)
            {
                int n = i;
                Assert.IsTrue(await Task.Run(() => SingleInstance.TrySend(name, $@"C:\{n}.jpg")));
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (paths.Count < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            CollectionAssert.AreEqual(new[] { @"C:\0.jpg", @"C:\1.jpg", @"C:\2.jpg" }, paths.ToArray());
        }

        [TestMethod]
        public void Send_Fails_When_Nobody_Is_Listening()
        {
            // Waits for the connect timeout (a few seconds)
            Assert.IsFalse(SingleInstance.TrySend(UniqueName(), @"C:\a.jpg"));
        }
    }
}
