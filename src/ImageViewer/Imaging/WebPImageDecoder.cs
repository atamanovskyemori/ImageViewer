using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Interop;

namespace ImageViewer.Imaging;

/// <summary>
/// libwebp.dll を使った WebP デコーダ。
/// OSにWebP拡張機能がなくてWICで読めなかったときのフォールバック。
/// 静止画用。アニメーションWebPは <see cref="WebPAnimationDecoder"/> が先に受け持つ。
/// 画面よりずっと大きい画像は、WIC と同じく、画面の大きさまで縮めながら読む（libwebp の use_scaling）。
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

    public DecodedImage Decode(byte[] data, int maxPixelSize, Action<DecodedImage>? firstFrame = null)
    {
        var (width, height) = ReadSize(data);
        if (ReducedSize(width, height, maxPixelSize) is { } reduced)
        {
            return new DecodedImage(DecodeScaled(data, reduced.Width, reduced.Height), null, width, height);
        }
        return new DecodedImage(DecodeFull(data, width, height));
    }

    /// <summary>
    /// 静止画の WebP を BitmapSource（Freeze 済み）にする。サムネイル作成からも使う。
    /// maxPixelSize を渡すと、長い辺がそこまでになるように縮めながら読む（小さい画像は拡大しない）。
    /// </summary>
    public static BitmapSource DecodeBitmap(byte[] data, int maxPixelSize = 0)
    {
        var (width, height) = ReadSize(data);
        if (maxPixelSize > 0 && Math.Max(width, height) > maxPixelSize)
        {
            var (w, h) = Fit(width, height, maxPixelSize);
            return DecodeScaled(data, w, h);
        }
        return DecodeFull(data, width, height);
    }

    /// <summary>
    /// 縮めて読むなら、その大きさ。WIC と同じく、長い辺が maxPixelSize の 1/<see cref="WicImageDecoder.MinReduction"/> 倍を
    /// 超えるときだけ縮める（少ししか縮まないなら、そのまま読んだほうが拡大したときに読み直さずに済む）
    /// </summary>
    internal static (int Width, int Height)? ReducedSize(int width, int height, int maxPixelSize) =>
        maxPixelSize > 0 && Math.Max(width, height) * WicImageDecoder.MinReduction > maxPixelSize
            ? Fit(width, height, maxPixelSize)
            : null;

    /// <summary>長い辺を maxPixelSize にした大きさ（縦横比はそのまま、短い辺は 1 以上）</summary>
    private static (int Width, int Height) Fit(int width, int height, int maxPixelSize)
    {
        double scale = (double)maxPixelSize / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static unsafe (int Width, int Height) ReadSize(byte[] data)
    {
        fixed (byte* src = data)
        {
            int width, height;
            if (LibWebP.WebPGetInfo(src, (nuint)data.Length, &width, &height) == 0)
            {
                throw new NotSupportedException("WebPのヘッダを解析できませんでした。");
            }

            if (width <= 0 || height <= 0 || (long)width * height * 4 > Array.MaxLength)
            {
                throw new NotSupportedException($"画像サイズが大きすぎます（{width}×{height}）。");
            }
            return (width, height);
        }
    }

    private static unsafe BitmapSource DecodeFull(byte[] data, int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[(long)stride * height];

        fixed (byte* src = data)
        fixed (byte* dst = pixels)
        {
            if (LibWebP.WebPDecodeBGRAInto(src, (nuint)data.Length, dst, (nuint)pixels.Length, stride) == null)
            {
                // アニメーションWebPは WebPDecode 系では読めない（libwebpdemux.dll が無いとここに来る）
                throw new NotSupportedException("WebPのデコードに失敗しました（アニメーションWebPには libwebpdemux.dll が必要）。");
            }
        }

        return Create(width, height, pixels, stride);
    }

    /// <summary>
    /// width × height に縮めながらデコードする。元の大きさの画像はメモリに作らない。
    /// 構造体の並びが DLL と合っていない（ありえないはずだが）ときは、元の大きさで読んでから縮める
    /// </summary>
    private static unsafe BitmapSource DecodeScaled(byte[] data, int width, int height)
    {
        if (sizeof(LibWebP.WebPDecoderConfig) != LibWebP.ExpectedDecoderConfigSize)
        {
            var (fullWidth, fullHeight) = ReadSize(data);
            return ThumbnailDecoder.Downscale(DecodeFull(data, fullWidth, fullHeight), Math.Max(width, height));
        }

        LibWebP.WebPDecoderConfig config;
        if (LibWebP.WebPInitDecoderConfigInternal(&config, LibWebP.DecoderAbiVersion) == 0)
        {
            throw new NotSupportedException("libwebp.dll のバージョンが合いません。");
        }

        int stride = width * 4;
        var pixels = new byte[(long)stride * height];

        fixed (byte* src = data)
        fixed (byte* dst = pixels)
        {
            config.Options.UseScaling = 1;
            config.Options.ScaledWidth = width;
            config.Options.ScaledHeight = height;

            // 出力先は C# の配列（libwebp にはメモリを確保させない）
            config.Output.Colorspace = LibWebP.ModeBgra;
            config.Output.IsExternalMemory = 1;
            config.Output.Rgba = dst;
            config.Output.Stride = stride;
            config.Output.Size = (nuint)pixels.Length;

            int status = LibWebP.WebPDecode(src, (nuint)data.Length, &config);
            LibWebP.WebPFreeDecBuffer(&config.Output);
            if (status != LibWebP.StatusOk)
            {
                throw new NotSupportedException($"WebPのデコードに失敗しました（{status}）。");
            }
        }

        return Create(width, height, pixels, stride);
    }

    // libwebp の出力は非乗算済みアルファなので Bgra32（Pbgra32 ではない）
    private static BitmapSource Create(int width, int height, byte[] pixels, int stride)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
