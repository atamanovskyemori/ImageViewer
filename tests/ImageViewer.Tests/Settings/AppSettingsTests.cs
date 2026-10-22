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
        public void Memory_Limits_Default_And_Are_Always_Written()
        {
            var settings = new AppSettings();

            Assert.AreEqual(256, settings.AnimationMaxMemoryMB);
            Assert.AreEqual(512, settings.PrefetchMaxMemoryMB);

            // Written even when they are defaults, so that they can be edited by hand
            var ini = settings.ToIni();
            Assert.AreEqual("256", ini.Get("Memory", "AnimationMaxMB"));
            Assert.AreEqual("512", ini.Get("Memory", "PrefetchMaxMB"));
        }

        [TestMethod]
        public void Memory_Limits_Are_Read_From_Ini()
        {
            var settings = AppSettings.FromIni(IniFile.Parse("[Memory]\nAnimationMaxMB=1024\nPrefetchMaxMB=64\n"));

            Assert.AreEqual(1024, settings.AnimationMaxMemoryMB);
            Assert.AreEqual(64, settings.PrefetchMaxMemoryMB);
        }

        [TestMethod]
        public void Out_Of_Range_Memory_Limits_Fall_Back_To_Defaults()
        {
            var settings = AppSettings.FromIni(IniFile.Parse("[Memory]\nAnimationMaxMB=0\nPrefetchMaxMB=999999\n"));

            Assert.AreEqual(AppSettings.DefaultAnimationMaxMemoryMB, settings.AnimationMaxMemoryMB);
            Assert.AreEqual(AppSettings.DefaultPrefetchMaxMemoryMB, settings.PrefetchMaxMemoryMB);
        }

        [TestMethod]
        public void Memory_Limits_RoundTrip_Through_File()
        {
            var path = Path.Combine(_dir, AppSettings.FileName);
            new AppSettings { AnimationMaxMemoryMB = 128, PrefetchMaxMemoryMB = 2048 }.TrySave(path);

            var loaded = AppSettings.Load(path);

            Assert.AreEqual(128, loaded.AnimationMaxMemoryMB);
            Assert.AreEqual(2048, loaded.PrefetchMaxMemoryMB);
        }

        [TestMethod]
        public void Thumbnail_Settings_Default_And_Are_Always_Written()
        {
            var settings = new AppSettings();

            Assert.AreEqual(256, settings.ThumbnailMaxMemoryMB);
            Assert.AreEqual(160, settings.ThumbnailSize);

            var ini = settings.ToIni();
            Assert.AreEqual("256", ini.Get("Memory", "ThumbnailMaxMB"));
            Assert.AreEqual("160", ini.Get("Thumbnail", "Size"));
        }

        [TestMethod]
        public void Thumbnail_Settings_RoundTrip_Through_File()
        {
            var path = Path.Combine(_dir, AppSettings.FileName);
            new AppSettings { ThumbnailMaxMemoryMB = 64, ThumbnailSize = 224 }.TrySave(path);

            var loaded = AppSettings.Load(path);

            Assert.AreEqual(64, loaded.ThumbnailMaxMemoryMB);
            Assert.AreEqual(224, loaded.ThumbnailSize);
        }

        [TestMethod]
        public void Hand_Written_Thumbnail_Size_Snaps_To_Nearest()
        {
            Assert.AreEqual(160, AppSettings.FromIni(IniFile.Parse("[Thumbnail]\nSize=150\n")).ThumbnailSize);
            Assert.AreEqual(256, AppSettings.FromIni(IniFile.Parse("[Thumbnail]\nSize=9999\n")).ThumbnailSize);
            Assert.AreEqual(160, AppSettings.FromIni(IniFile.Parse("[Thumbnail]\nSize=big\n")).ThumbnailSize, "Unreadable value keeps the default");
        }

        [TestMethod]
        public void Out_Of_Range_Thumbnail_Memory_Falls_Back_To_Default()
        {
            var settings = AppSettings.FromIni(IniFile.Parse("[Memory]\nThumbnailMaxMB=0\n"));

            Assert.AreEqual(AppSettings.DefaultThumbnailMaxMemoryMB, settings.ThumbnailMaxMemoryMB);
        }

        [TestMethod]
        public void Slideshow_Interval_Defaults_And_Is_Always_Written()
        {
            var settings = new AppSettings();

            Assert.AreEqual(5.0, settings.SlideshowIntervalSeconds);
            Assert.AreEqual("5", settings.ToIni().Get("Slideshow", "IntervalSeconds"));
        }

        [TestMethod]
        public void Slideshow_Interval_Accepts_Decimals()
        {
            var settings = AppSettings.FromIni(IniFile.Parse("[Slideshow]\nIntervalSeconds=2.5\n"));

            Assert.AreEqual(2.5, settings.SlideshowIntervalSeconds);
            Assert.AreEqual("2.5", settings.ToIni().Get("Slideshow", "IntervalSeconds"));
        }

        [TestMethod]
        [DataRow("0")]
        [DataRow("0.1")]
        [DataRow("99999")]
        [DataRow("fast")]
        [DataRow("NaN")]
        public void Out_Of_Range_Slideshow_Interval_Falls_Back_To_Default(string value)
        {
            var settings = AppSettings.FromIni(IniFile.Parse($"[Slideshow]\nIntervalSeconds={value}\n"));

            Assert.AreEqual(AppSettings.DefaultSlideshowIntervalSeconds, settings.SlideshowIntervalSeconds);
        }

        [TestMethod]
        public void Slideshow_Interval_RoundTrips_Through_File()
        {
            var path = Path.Combine(_dir, AppSettings.FileName);
            new AppSettings { SlideshowIntervalSeconds = 12.25 }.TrySave(path);

            Assert.AreEqual(12.25, AppSettings.Load(path).SlideshowIntervalSeconds);
        }

        [TestMethod]
        public void InfoPanel_Width_Defaults_RoundTrips_And_Is_Clamped()
        {
            Assert.AreEqual(300, new AppSettings().InfoPanelWidth);
            Assert.AreEqual("300", new AppSettings().ToIni().Get("InfoPanel", "Width"));

            var path = Path.Combine(_dir, AppSettings.FileName);
            new AppSettings { InfoPanelWidth = 480 }.TrySave(path);
            Assert.AreEqual(480, AppSettings.Load(path).InfoPanelWidth);

            Assert.AreEqual(AppSettings.MinInfoPanelWidth, AppSettings.FromIni(IniFile.Parse("[InfoPanel]\nWidth=50\n")).InfoPanelWidth);
            Assert.AreEqual(AppSettings.MaxInfoPanelWidth, AppSettings.FromIni(IniFile.Parse("[InfoPanel]\nWidth=99999\n")).InfoPanelWidth);
            Assert.AreEqual(300, AppSettings.FromIni(IniFile.Parse("[InfoPanel]\nWidth=wide\n")).InfoPanelWidth);
        }

        [TestMethod]
        public void Missing_Maximized_Means_Not_Maximized()
        {
            var ini = IniFile.Parse("[Window]\nLeft=0\nTop=0\nWidth=800\nHeight=600\n");

            Assert.IsFalse(AppSettings.FromIni(ini).Window!.Maximized);
        }

        [TestMethod]
        public void SingleInstance_Defaults_To_True_And_Is_Always_Written()
        {
            var settings = AppSettings.FromIni(new IniFile());

            Assert.IsTrue(settings.SingleInstance);
            Assert.AreEqual("true", settings.ToIni().Get("Startup", "SingleInstance"));
        }

        [TestMethod]
        [DataRow("false")]
        [DataRow("0")]
        public void SingleInstance_Can_Be_Turned_Off(string value)
        {
            var settings = AppSettings.FromIni(IniFile.Parse($"[Startup]\nSingleInstance={value}\n"));

            Assert.IsFalse(settings.SingleInstance);
            Assert.AreEqual("false", settings.ToIni().Get("Startup", "SingleInstance"));
        }

        [TestMethod]
        public void Image_Background_Defaults_And_Is_Always_Written()
        {
            var settings = AppSettings.FromIni(new IniFile());

            Assert.AreEqual(ImageBackground.Default, settings.ImageBackground);
            Assert.AreEqual("Default", settings.ToIni().Get("View", "Background"));
        }

        [TestMethod]
        [DataRow("Checker", ImageBackground.Checker)]
        [DataRow("white", ImageBackground.White)]
        [DataRow("Rainbow", ImageBackground.Default)]
        [DataRow("99", ImageBackground.Default)]
        public void Image_Background_Is_Read(string value, ImageBackground expected)
        {
            var settings = AppSettings.FromIni(IniFile.Parse($"[View]\nBackground={value}\n"));

            Assert.AreEqual(expected, settings.ImageBackground);
        }

        [TestMethod]
        public void Broken_SingleInstance_Falls_Back_To_True()
        {
            var settings = AppSettings.FromIni(IniFile.Parse("[Startup]\nSingleInstance=maybe\n"));

            Assert.IsTrue(settings.SingleInstance);
        }
    }
}
