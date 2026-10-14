using System.IO;
using ImageViewer.Navigation;
using ImageViewer.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Settings
{
    [TestClass]
    public class AppSettingsTests
    {
        private string _dir = null!;

        [TestInitialize]
        public void Initialize()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ImageViewer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // Leftover temp files are harmless
            }
        }

        [TestMethod]
        public void Load_Returns_Defaults_When_File_Is_Missing()
        {
            var settings = AppSettings.Load(Path.Combine(_dir, "missing.ini"));

            Assert.IsNull(settings.Window);
            Assert.AreEqual(FileSortKey.Name, settings.SortKey);
            Assert.IsFalse(settings.SortDescending);
        }

        [TestMethod]
        public void Save_Then_Load_RoundTrips()
        {
            var path = Path.Combine(_dir, AppSettings.FileName);
            var original = new AppSettings
            {
                Window = new WindowBounds(-1200, 40, 1600, 900, Maximized: true),
                SortKey = FileSortKey.LastWriteTime,
                SortDescending = true,
            };

            Assert.IsTrue(original.TrySave(path));
            Assert.IsFalse(File.Exists(path + ".tmp"), "Temp file should be gone after saving");

            var loaded = AppSettings.Load(path);

            Assert.AreEqual(original.Window, loaded.Window);
            Assert.AreEqual(FileSortKey.LastWriteTime, loaded.SortKey);
            Assert.IsTrue(loaded.SortDescending);
        }

        [TestMethod]
        public void Save_Overwrites_Existing_File()
        {
            var path = Path.Combine(_dir, AppSettings.FileName);
            new AppSettings { SortKey = FileSortKey.Size }.TrySave(path);
            new AppSettings { SortKey = FileSortKey.LastWriteTime }.TrySave(path);

            Assert.AreEqual(FileSortKey.LastWriteTime, AppSettings.Load(path).SortKey);
        }

        [TestMethod]
        public void Hand_Written_Values_Are_Accepted()
        {
            var ini = IniFile.Parse("[Window]\nLeft=0\nTop=0\nWidth=800\nHeight=600\nMaximized=1\n[Sort]\nKey=size\nDescending=0\n");

            var settings = AppSettings.FromIni(ini);

            Assert.AreEqual(new WindowBounds(0, 0, 800, 600, true), settings.Window);
            Assert.AreEqual(FileSortKey.Size, settings.SortKey, "Enum names should be case-insensitive");
            Assert.IsFalse(settings.SortDescending);
        }

        [TestMethod]
        public void Broken_Values_Fall_Back_To_Defaults()
        {
            var ini = IniFile.Parse("[Window]\nLeft=abc\nTop=0\nWidth=800\nHeight=600\n[Sort]\nKey=Unknown\nDescending=maybe\n");

            var settings = AppSettings.FromIni(ini);

            Assert.IsNull(settings.Window, "Window with an unreadable value should be ignored as a whole");
            Assert.AreEqual(FileSortKey.Name, settings.SortKey);
            Assert.IsFalse(settings.SortDescending);
        }

        [TestMethod]
        public void Undefined_Numeric_SortKey_Is_Ignored()
        {
            var settings = AppSettings.FromIni(IniFile.Parse("[Sort]\nKey=99\n"));

            Assert.AreEqual(FileSortKey.Name, settings.SortKey);
        }

        [TestMethod]
        public void Too_Small_Window_Is_Ignored()
        {
            var ini = IniFile.Parse("[Window]\nLeft=0\nTop=0\nWidth=0\nHeight=600\n");

            Assert.IsNull(AppSettings.FromIni(ini).Window);
        }

        [TestMethod]
        public void Missing_Maximized_Means_Not_Maximized()
        {
            var ini = IniFile.Parse("[Window]\nLeft=0\nTop=0\nWidth=800\nHeight=600\n");

            Assert.IsFalse(AppSettings.FromIni(ini).Window!.Maximized);
        }
    }
}
