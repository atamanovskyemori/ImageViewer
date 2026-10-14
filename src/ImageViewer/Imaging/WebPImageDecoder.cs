using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Interop;

namespace ImageViewer.Imaging;

/// <summary>
/// libwebp.dll を使った WebP デコーダ。
/// OSにWebP拡張機能がなくてWICで読めなかったときのフォールバック。
/// 現状は静止画のみ（アニメーションWebPは非対応）。
/// </summary>
public sealed class WebPImageDecoder : IImageDecoder
{
    public string Name => "libwebp";

    public bool IsAvailable => LibWebP.IsAvailable;

    /// <summary>"RIFF" .... "WEBP" で始まるか</summary>
    public bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 12
        && header[..4].SequenceEqual("RIFF"u8)
        && header.Slice(8, 4).SequenceEqual("WEBP"u8);

    public unsafe BitmapSource Decode(byte[] data)
    {
        fixed (byte* src = data)
        {
            int width, height;
            if (LibWebP.WebPGetInfo(src, (nuint)data.Length, &width, &height) == 0)
            {
                throw new NotSupportedException("WebPのヘッダを解析できませんでした。");
            }

            long size = (long)width * height * 4;
            if (width <= 0 || height <= 0 || size > Array.MaxLength)
            {
                throw new NotSupportedException($"画像サイズが大きすぎます（{width}×{height}）。");
            }

            int stride = width * 4;
            var pixels = new byte[size];

            fixed (byte* dst = pixels)
            {
                if (LibWebP.WebPDecodeBGRAInto(src, (nuint)data.Length, dst, (nuint)pixels.Length, stride) == null)
                {
                    throw new NotSupportedException("WebPのデコードに失敗しました（アニメーションWebPは未対応）。");
                }
            }

            // libwebp の出力は非乗算済みアルファなので Bgra32（Pbgra32 ではない）
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
