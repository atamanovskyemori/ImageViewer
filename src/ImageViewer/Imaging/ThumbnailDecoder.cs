using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Interop;

namespace ImageViewer.Imaging;

/// <summary>サムネイルを作る処理のインターフェース（テストで差し替えるため）</summary>
public interface IThumbnailDecoder
{
    /// <summary>
    /// 長い辺が maxPixelSize 以下になるように縮小した画像を返す（小さい画像は拡大しない）。
    /// バックグラウンドスレッドから呼ばれるので、結果は Freeze 済みにすること。
    /// 読めなければ例外を投げる。
    /// </summary>
    BitmapSource Decode(string path, int maxPixelSize);
}

/// <summary>
/// サムネイル作成。1枚表示と同じく「アニメーションWebP → WIC → libwebp」の順で試すが、
/// 全部のコマは読まず、できるだけ小さく読む。
/// <list type="bullet">
/// <item>WIC はデコード時に縮小させる（DecodePixelWidth）。JPEG はデコーダが縮小しながら読むので速い</item>
/// <item>JPEG / TIFF は EXIF の向きを反映する（1枚表示と同じ）</item>
/// <item>アニメーション（GIF / WebP）は1コマ目だけ。ICO は一番大きいアイコン</item>
/// </list>
/// 結果は 96dpi・Bgra32 にそろえる（ファイルの dpi で表示サイズが変わらないように）。
/// </summary>
public sealed class ThumbnailDecoder : IThumbnailDecoder
{
    public BitmapSource Decode(string path, int maxPixelSize)
    {
        if (maxPixelSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPixelSize));
        }

        var data = File.ReadAllBytes(path);

        // WIC の WebP コーデックが無い環境でも出せるように、アニメーションWebP は先に libwebpdemux で読む
        if (WebPAnimationDecoder.IsAnimatedWebP(data) && LibWebPDemux.IsAvailable)
        {
            return Normalize(Downscale(WebPAnimationDecoder.DecodeFirstFrame(data), maxPixelSize));
        }

        try
        {
            return DecodeWithWic(data, maxPixelSize);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && IsWebP(data) && LibWebP.IsAvailable)
        {
            // OS に WebP 拡張機能が無い。libwebp で全体を読んでから縮める
            return Normalize(Downscale(WebPImageDecoder.DecodeBitmap(data), maxPixelSize));
        }
    }

    private static bool IsWebP(ReadOnlySpan<byte> data) =>
        data.Length >= 12
        && data[..4].SequenceEqual("RIFF"u8)
        && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>WIC で縮小デコードする。stream を閉じる前にピクセルを取り出し終えるよう、Normalize までここでやる</summary>
    private static BitmapSource DecodeWithWic(byte[] data, int maxPixelSize)
    {
        using var stream = new MemoryStream(data, writable: false);

        // まずヘッダだけ読んで、大きさ・向き・形式を調べる（CacheOption.None なのでピクセルはまだ読まない）
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
        var frame = decoder.Frames[0];

        // ICO は小さいアイコンから入っていることが多いので、一番大きいものを縮めて使う
        if (decoder is IconBitmapDecoder)
        {
            var largest = decoder.Frames.MaxBy(f => (long)f.PixelWidth * f.PixelHeight) ?? frame;
            return Normalize(Downscale(largest, maxPixelSize));
        }

        int orientation = WicImageDecoder.OrientationQueryFor(decoder) is { } query
            ? WicImageDecoder.ReadOrientation(frame, query)
            : 1;

        // 縮小しながらデコードする。長い辺だけ指定すれば、縦横比は保たれる
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelWidth >= frame.PixelHeight)
        {
            image.DecodePixelWidth = Math.Min(maxPixelSize, frame.PixelWidth);
        }
        else
        {
            image.DecodePixelHeight = Math.Min(maxPixelSize, frame.PixelHeight);
        }
        image.EndInit();

        return Normalize(WicImageDecoder.ApplyOrientation(image, orientation));
    }

    /// <summary>長い辺が maxPixelSize を超えていたら縮小する（拡大はしない）</summary>
    internal static BitmapSource Downscale(BitmapSource source, int maxPixelSize)
    {
        int longSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longSide <= maxPixelSize)
        {
            return source;
        }

        double scale = (double)maxPixelSize / longSide;
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();

        // TransformedBitmap は描画時に遅延計算されるので、CachedBitmap でここ（裏スレッド）で確定させる
        return new CachedBitmap(new TransformedBitmap(source, transform), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    /// <summary>
    /// 96dpi・Bgra32 の新しい画像にコピーして Freeze する。
    /// ファイルの dpi（72dpi など）のままだと、WPF の表示サイズが画像ごとにばらばらになるので。
    /// </summary>
    internal static BitmapSource Normalize(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[(long)stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
