using System.IO;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// WPF標準（Windows Imaging Component）によるデコーダ。
/// JPEG/PNG/GIF/BMP/TIFF/ICO/JPEG XR は標準で読める。
/// WebP/HEIC などは、OSにコーデック（拡張機能）が入っていれば読める。
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    public string Name => "WIC";

    public bool IsAvailable => true;

    // WICは中身を見て自分で判断するので、とりあえず何でも試す
    public bool CanDecode(ReadOnlySpan<byte> header) => true;

    public BitmapSource Decode(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);

        // OnLoad で全部読み込んでおけば、stream を閉じても大丈夫
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        BitmapSource frame = decoder.Frames[0];
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }
        return frame;
    }
}
