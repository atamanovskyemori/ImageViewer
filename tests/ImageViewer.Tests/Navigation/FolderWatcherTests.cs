using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ImageViewer.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Navigation
{
    [TestClass]
    public class FolderWatcherTests
    {
        private string _root = null!;

        [TestInitialize]
        public void Initialize()
        {
            _root = Path.Combine(Path.GetTempPath(), "ImageViewer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Leftover temp files are harmless
            }
        }

        /// <summary>Pumps the current dispatcher until the condition holds or the timeout passes.</summary>
        private static bool PumpUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            return condition();
        }

        [TestMethod]
        public void New_Image_Is_Reported_Once_After_Writes_Settle()
        {
            using var watcher = new FolderWatcher(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3));
            int changes = 0;
            watcher.Changed += (_, _) => changes++;
            watcher.Watch(_root);

            File.WriteAllBytes(Path.Combine(_root, "new.png"), new byte[10]);
            File.WriteAllBytes(Path.Combine(_root, "new2.png"), new byte[10]);

            Assert.IsTrue(PumpUntil(() => changes > 0, 5000), "No change was reported");

            // Both files were written in quick succession, so they are reported together
            PumpUntil(() => false, 500);
            Assert.AreEqual(1, changes);
        }

        [TestMethod]
        public void Unsupported_Files_Are_Ignored()
        {
            using var watcher = new FolderWatcher(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));
            int changes = 0;
            watcher.Changed += (_, _) => changes++;
            watcher.Watch(_root);

            File.WriteAllText(Path.Combine(_root, "note.txt"), "hello");

            Assert.IsFalse(PumpUntil(() => changes > 0, 700));
        }

        [TestMethod]
        public void Stops_Reporting_After_Watching_Another_Folder()
        {
            var other = Path.Combine(_root, "other");
            Directory.CreateDirectory(other);

            using var watcher = new FolderWatcher(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));
            int changes = 0;
            watcher.Changed += (_, _) => changes++;
            watcher.Watch(_root);
            watcher.Watch(other);
            Assert.AreEqual(other, watcher.Directory);

            File.WriteAllBytes(Path.Combine(_root, "a.jpg"), new byte[10]);
            Assert.IsFalse(PumpUntil(() => changes > 0, 700));

            File.WriteAllBytes(Path.Combine(other, "b.jpg"), new byte[10]);
            Assert.IsTrue(PumpUntil(() => changes > 0, 5000));
        }

        [TestMethod]
        public void Missing_Folder_Is_Not_Watched_Without_Throwing()
        {
            using var watcher = new FolderWatcher(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));
            watcher.Watch(Path.Combine(_root, "missing"));
        }
    }
}
