using System.IO;
using ImageViewer.Imaging;
using ImageViewer.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Imaging
{
    [TestClass]
    public class ImageLoaderTests
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

        // A decoder that accepts everything and always fails, like WIC without the codec installed.
        private sealed class FailingDecoder : IImageDecoder
        {
            public string Name => "fail";
            public bool IsAvailable => true;
            public bool CanDecode(ReadOnlySpan<byte> header) => true;
            public DecodedImage Decode(byte[] data, int maxPixelSize, Action<DecodedImage>? firstFrame = null) => throw new NotSupportedException("no codec");
        }

        private string LoadError(string fileName)
        {
            var path = Path.Combine(_root, fileName);
            File.WriteAllBytes(path, [1, 2, 3]);

            var loader = new ImageLoader([new FailingDecoder()]);
            return Assert.ThrowsExactly<ImageLoadException>(() => loader.Load(path)).Message;
        }

        [TestMethod]
        [DataRow("photo.heic")]
        [DataRow("photo.HEIF")]
        [DataRow("photo.avif")]
        [DataRow("photo.jxl")]
        public void Codec_Formats_Mention_The_Store_Extension(string fileName)
        {
            StringAssert.Contains(LoadError(fileName), "Microsoft Store");
        }

        [TestMethod]
        public void Other_Formats_Do_Not_Mention_The_Store_Extension()
        {
            Assert.IsFalse(LoadError("photo.jpg").Contains("Microsoft Store"));
        }

        [TestMethod]
        [DataRow("a.heic")]
        [DataRow("a.heif")]
        [DataRow("a.hif")]
        [DataRow("a.avif")]
        [DataRow("a.jxl")]
        [DataRow("a.jpg")]
        public void Codec_Formats_Are_Listed_In_Folders(string fileName)
        {
            Assert.IsTrue(FolderNavigator.IsSupported(fileName));
        }
    }
}
