using System.IO;
using ImageViewer.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Navigation
{
    [TestClass]
    public class FolderSiblingsTests
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
                foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(dir, FileAttributes.Directory); // clear Hidden so that Delete works everywhere
                }
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Leftover temp files are harmless
            }
        }

        /// <summary>Creates a folder under the root ("a/b" for nested folders), with the given (empty) files.</summary>
        private string Folder(string name, params string[] files)
        {
            var path = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            foreach (var file in files)
            {
                File.WriteAllBytes(Path.Combine(path, file), []);
            }
            return path;
        }

        private string? Next(string folder) => FolderSiblings.Find(folder, _root, +1);

        private string? Previous(string folder) => FolderSiblings.Find(folder, _root, -1);

        [TestMethod]
        public void Folders_Are_In_Natural_Name_Order()
        {
            var a2 = Folder("album2", "a.jpg");
            var a10 = Folder("album10", "a.jpg");
            var a1 = Folder("album1", "a.jpg");

            Assert.AreEqual(a2, Next(a1));
            Assert.AreEqual(a10, Next(a2));
            Assert.AreEqual(a2, Previous(a10));
            Assert.AreEqual(a1, Previous(a2));
        }

        [TestMethod]
        public void Subfolders_Are_Visited_Depth_First()
        {
            // a, a/a1, a/a1/x, a/a2, b
            var a = Folder("a", "1.jpg");
            var a1 = Folder("a/a1", "1.jpg");
            var x = Folder("a/a1/x", "1.jpg");
            var a2 = Folder("a/a2", "1.jpg");
            var b = Folder("b", "1.jpg");

            Assert.AreEqual(a1, Next(a), "A folder is followed by its first subfolder");
            Assert.AreEqual(x, Next(a1));
            Assert.AreEqual(a2, Next(x), "After the deepest folder, back up to the next sibling");
            Assert.AreEqual(b, Next(a2), "After the last subfolder, the next folder on the original level");
            Assert.IsNull(Next(b));
        }

        [TestMethod]
        public void Previous_Is_The_Exact_Reverse()
        {
            var a = Folder("a", "1.jpg");
            var a1 = Folder("a/a1", "1.jpg");
            var x = Folder("a/a1/x", "1.jpg");
            var a2 = Folder("a/a2", "1.jpg");
            var b = Folder("b", "1.jpg");

            Assert.AreEqual(a2, Previous(b), "The previous folder of b is the last folder inside a");
            Assert.AreEqual(x, Previous(a2));
            Assert.AreEqual(a1, Previous(x), "The previous folder of a first child is its parent");
            Assert.AreEqual(a, Previous(a1));
            Assert.IsNull(Previous(a));
        }

        [TestMethod]
        public void Folders_Without_Images_Are_Skipped_Even_Inside_Subfolders()
        {
            var a = Folder("a", "1.jpg");
            Folder("b");                       // no images, but has a subfolder with images
            var deep = Folder("b/c/d", "1.png");
            Folder("e", "readme.txt");         // no supported images
            var f = Folder("f", "1.webp");

            Assert.AreEqual(deep, Next(a));
            Assert.AreEqual(f, Next(deep));
            Assert.AreEqual(deep, Previous(f));
            Assert.AreEqual(a, Previous(deep), "Empty parents are skipped on the way back too");
        }

        [TestMethod]
        public void Does_Not_Go_Above_The_Scope_Root()
        {
            var inside = Folder("scope/a", "1.jpg");
            Folder("outside", "1.jpg");
            var scope = Path.Combine(_root, "scope");

            Assert.IsNull(FolderSiblings.Find(inside, scope, +1));
            Assert.IsNull(FolderSiblings.Find(inside, scope, -1));
            Assert.IsNull(FolderSiblings.Find(Path.Combine(_root, "outside"), scope, +1), "Folder outside the scope");
        }

        [TestMethod]
        public void Scope_Root_Itself_Descends_Into_Its_Folders()
        {
            // e.g. a whole drive was opened: the scope is the folder itself
            var a = Folder("a", "1.jpg");

            Assert.AreEqual(a, FolderSiblings.Find(_root, _root, +1));
            Assert.IsNull(FolderSiblings.Find(_root, _root, -1));
        }

        [TestMethod]
        public void Hidden_Folders_Are_Not_Entered()
        {
            var a = Folder("a", "1.jpg");
            var hidden = Folder("b", "1.jpg");
            Folder("b/inner", "1.jpg");
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
            var c = Folder("c", "1.jpg");

            Assert.AreEqual(c, Next(a));
            Assert.AreEqual(a, Previous(c));
        }

        [TestMethod]
        public void Works_From_A_Hidden_Folder_Itself()
        {
            var a = Folder("a", "1.jpg");
            var hidden = Folder("b", "1.jpg");
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
            var c = Folder("c", "1.jpg");

            Assert.AreEqual(a, Previous(hidden));
            Assert.AreEqual(c, Next(hidden));
        }

        [TestMethod]
        public void Trailing_Separator_And_Case_Do_Not_Matter()
        {
            var a = Folder("a", "1.jpg");
            var b = Folder("b", "1.jpg");

            // The returned path keeps the casing that was passed in, so compare case-insensitively (like Windows does)
            Assert.AreEqual(b, FolderSiblings.Find(a.ToUpperInvariant() + Path.DirectorySeparatorChar, _root, +1), ignoreCase: true);
        }

        [TestMethod]
        public void Missing_Folder_Returns_Null()
        {
            Folder("a", "1.jpg");

            Assert.IsNull(Next(Path.Combine(_root, "gone")));
        }

        [TestMethod]
        public void Custom_Image_Check_Is_Used()
        {
            var a = Folder("a");
            var b = Folder("b");
            var c = Folder("c");

            Assert.AreEqual(c, FolderSiblings.Find(a, _root, +1, path => path == c));
            Assert.IsNull(FolderSiblings.Find(b, _root, 0), "No direction");
        }

        [TestMethod]
        public void DisplayName_Is_Relative_To_The_Scope()
        {
            var deep = Folder("a/b");

            Assert.AreEqual(Path.Combine("a", "b"), FolderSiblings.DisplayName(deep, _root));
            Assert.AreEqual("a", FolderSiblings.DisplayName(Path.Combine(_root, "a"), _root));
        }

        [TestMethod]
        public void Navigator_Opens_Directory_At_First_Or_Last()
        {
            var folder = Folder("a", "img1.jpg", "img2.jpg", "img10.jpg");
            var navigator = new FolderNavigator();

            Assert.IsTrue(navigator.OpenDirectory(folder));
            Assert.AreEqual(0, navigator.Index);
            Assert.AreEqual(folder, navigator.CurrentDirectory);

            Assert.IsTrue(navigator.OpenDirectory(folder, selectLast: true));
            Assert.AreEqual(2, navigator.Index);
            Assert.AreEqual("img10.jpg", Path.GetFileName(navigator.Current));
        }

        [TestMethod]
        public void Navigator_CurrentDirectory_From_File()
        {
            var folder = Folder("a", "img1.jpg");
            var navigator = new FolderNavigator();

            Assert.IsNull(navigator.CurrentDirectory);
            navigator.OpenFile(Path.Combine(folder, "img1.jpg"));

            Assert.AreEqual(folder, navigator.CurrentDirectory);
        }

        [TestMethod]
        public void ContainsImages_Checks_Extensions()
        {
            Assert.IsTrue(FolderNavigator.ContainsImages(Folder("a", "x.txt", "y.JPG")));
            Assert.IsFalse(FolderNavigator.ContainsImages(Folder("b", "x.txt")));
            Assert.IsFalse(FolderNavigator.ContainsImages(Path.Combine(_root, "missing")));
        }
    }
}
