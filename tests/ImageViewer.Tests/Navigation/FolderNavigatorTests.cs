using System.IO;
using ImageViewer.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Navigation
{
    [TestClass]
    public class FolderNavigatorTests
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

        /// <summary>Creates a file of the given size under the root.</summary>
        private string CreateFile(string name, int size)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        private static string[] Names(FolderNavigator navigator) =>
            navigator.Files.Select(Path.GetFileName).ToArray()!;

        private FolderNavigator Open(FileSortKey key, bool descending)
        {
            var navigator = new FolderNavigator();
            navigator.SetSort(key, descending);
            Assert.IsTrue(navigator.OpenDirectory(_root));
            return navigator;
        }

        [TestMethod]
        public void Toggling_Direction_Keeps_Ties_In_Ascending_Name_Order()
        {
            CreateFile("b.jpg", 1);
            CreateFile("a.jpg", 1);
            CreateFile("c.jpg", 1);
            CreateFile("big.jpg", 10);

            var navigator = Open(FileSortKey.Size, descending: false);
            CollectionAssert.AreEqual(new[] { "a.jpg", "b.jpg", "c.jpg", "big.jpg" }, Names(navigator));

            navigator.SetSort(FileSortKey.Size, descending: true);
            CollectionAssert.AreEqual(new[] { "big.jpg", "a.jpg", "b.jpg", "c.jpg" }, Names(navigator));
        }

        [TestMethod]
        public void Toggling_Direction_Matches_Opening_In_That_Direction()
        {
            CreateFile("img2.jpg", 5);
            CreateFile("img10.jpg", 5);
            CreateFile("img1.jpg", 3);
            CreateFile("img3.jpg", 8);

            var toggled = Open(FileSortKey.Size, descending: false);
            toggled.SetSort(FileSortKey.Size, descending: true);
            toggled.SetSort(FileSortKey.Size, descending: false);
            toggled.SetSort(FileSortKey.Size, descending: true);

            var fresh = Open(FileSortKey.Size, descending: true);
            CollectionAssert.AreEqual(Names(fresh), Names(toggled));
        }

        [TestMethod]
        public void Toggling_Direction_Rereads_File_Info()
        {
            CreateFile("a.jpg", 1);
            CreateFile("b.jpg", 2);
            var c = CreateFile("c.jpg", 3);

            var navigator = Open(FileSortKey.LastWriteTime, descending: false);
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(_root, "a.jpg"), baseTime);
            File.SetLastWriteTimeUtc(Path.Combine(_root, "b.jpg"), baseTime.AddHours(1));
            File.SetLastWriteTimeUtc(c, baseTime.AddHours(2));
            navigator.SetSort(FileSortKey.LastWriteTime, descending: true);
            CollectionAssert.AreEqual(new[] { "c.jpg", "b.jpg", "a.jpg" }, Names(navigator));

            // c.jpg becomes the oldest while sorted
            File.SetLastWriteTimeUtc(c, baseTime.AddHours(-1));
            navigator.SetSort(FileSortKey.LastWriteTime, descending: false);
            CollectionAssert.AreEqual(new[] { "c.jpg", "a.jpg", "b.jpg" }, Names(navigator));
        }

        [TestMethod]
        public void Toggling_Direction_Keeps_Current_File()
        {
            CreateFile("a.jpg", 1);
            CreateFile("b.jpg", 1);
            CreateFile("c.jpg", 2);

            var navigator = Open(FileSortKey.Size, descending: false);
            navigator.MoveToIndex(1);
            Assert.AreEqual("b.jpg", Path.GetFileName(navigator.Current));

            navigator.SetSort(FileSortKey.Size, descending: true);
            Assert.AreEqual("b.jpg", Path.GetFileName(navigator.Current));
            Assert.AreEqual(2, navigator.Index);
        }
    }
}
