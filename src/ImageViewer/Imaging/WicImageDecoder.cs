using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// WPF標準（Windows Imaging Component）によるデコーダ。
/// JPEG/PNG/GIF/BMP/TIFF/ICO/JPEG XR は標準で読める。
/// WebP/HEIC などは、OSにコーデック（拡張機能）が入っていれば読める。
/// JPEG/TIFF は EXIF の向き（Orientation）を見て、正しい向きに直してから返す（表示用。ファイルは変えない）。
/// 2コマ以上ある GIF はアニメーションとして合成する（<see cref="GifAnimation"/>）。
/// 画面よりずっと大きい静止画は、画面の大きさまで縮めながら読める（メモリとデコード時間を減らすため）。
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    // EXIF の Orientation タグ（274）。System.Photo.Orientation で取れなかったときの予備
    private const string JpegOrientationQuery = "/app1/ifd/{ushort=274}";
    private const string TiffOrientationQuery = "/ifd/{ushort=274}";

    private readonly long _maxAnimationBytes;

    /// <param name="maxAnimationBytes">1枚のアニメーションに使っていいメモリ。超えたら1コマ目だけ表示する</param>
    public WicImageDecoder(long maxAnimationBytes = AnimationLimits.DefaultMaxBytes)
    {
        _maxAnimationBytes = maxAnimationBytes;
    }

    public string Name => "WIC";

    public bool IsAvailable => true;

    // WICは中身を見て自分で判断するので、とりあえず何でも試す
    public bool CanDecode(ReadOnlySpan<byte> header) => true;

    public DecodedImage Decode(byte[] data, int maxPixelSize)
    {
        using var stream = new MemoryStream(data, writable: false);

        // まずヘッダだけ読んで（None なのでピクセルはまだ読まない）、画面に合わせて縮めて読むか決める
        if (maxPixelSize > 0)
        {
            var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (ShouldReduce(probe, maxPixelSize))
            {
                return DecodeReduced(stream, probe, maxPixelSize);
            }
            stream.Position = 0;
        }

        // OnLoad で全部読み込んでおけば、stream を閉じても大丈夫
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        // アニメーションGIF（メタデータもここで読むので、stream を閉じる前に処理する）
        if (decoder is GifBitmapDecoder && decoder.Frames.Count > 1)
        {
            return GifAnimation.Decode(decoder, _maxAnimationBytes);
        }

        BitmapFrame frame = decoder.Frames[0];
        BitmapSource result = frame;

        // WICのJPEG/TIFFデコーダはEXIFの向きを反映しないので自分で回す。
        // ほかの形式（HEICなど）はコーデック側で反映済みのことがあるので、二重に回さないよう対象を絞る
        string? fallbackQuery = OrientationQueryFor(decoder);
        if (fallbackQuery is not null)
        {
            result = ApplyOrientation(frame, ReadOrientation(frame, fallbackQuery));
        }

        if (result.CanFreeze)
        {
            result.Freeze();
        }
        return new DecodedImage(result);
    }

    /// <summary>
    /// 縮めて読むか。長い辺が maxPixelSize の 1/<see cref="MinReduction"/> 倍を超える静止画だけ
    /// （少ししか縮まないなら、そのまま読んだほうが拡大したときに読み直さずに済む）。
    /// GIF（アニメーションかもしれない）・ICO（小さいアイコンの集まり）は縮めない。
    /// </summary>
    internal static bool ShouldReduce(BitmapDecoder decoder, int maxPixelSize)
    {
        if (decoder is GifBitmapDecoder or IconBitmapDecoder)
        {
            return false;
        }

        var frame = decoder.Frames[0];
        long longSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
        return longSide * MinReduction > maxPixelSize;
    }

    /// <summary>縮めて読むのは、元の大きさの何倍より小さくなるときか</summary>
    internal const double MinReduction = 0.8;

    /// <summary>
    /// 長い辺が maxPixelSize になるように縮めながらデコードする（JPEG はデコーダが縮めながら読むので速い）。
    /// EXIF の向きは元の大きさで読むときと同じように反映し、元の大きさ（向きを直した後）も一緒に返す。
    /// </summary>
    private static DecodedImage DecodeReduced(MemoryStream stream, BitmapDecoder probe, int maxPixelSize)
    {
        var frame = probe.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        int orientation = OrientationQueryFor(probe) is { } query ? ReadOrientation(frame, query) : 1;

        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (width >= height)
        {
            image.DecodePixelWidth = maxPixelSize; // 長い辺だけ指定すれば、縦横比は保たれる
        }
        else
        {
            image.DecodePixelHeight = maxPixelSize;
        }
        image.EndInit();

        BitmapSource result = ApplyOrientation(image, orientation);
        if (result.CanFreeze)
        {
            result.Freeze();
        }

        // 5〜8 は 90°回すので、縦横が入れ替わる
        bool swapped = orientation >= 5;
        return new DecodedImage(result, null, swapped ? height : width, swapped ? width : height);
    }

    /// <summary>
    /// EXIF の向きを自分で反映する形式なら、Orientation の予備クエリを返す。反映しない形式なら null。
    /// （サムネイル作成でも同じ判断をするので internal）
    /// </summary>
    internal static string? OrientationQueryFor(BitmapDecoder decoder) => decoder switch
    {
        JpegBitmapDecoder => JpegOrientationQuery,
        TiffBitmapDecoder => TiffOrientationQuery,
        _ => null,
    };

    /// <summary>EXIF の Orientation（1〜8）を読む。無い・読めないときは 1（そのまま）</summary>
    internal static int ReadOrientation(BitmapFrame frame, string fallbackQuery)
    {
        BitmapMetadata? metadata;
        try
        {
            metadata = frame.Metadata as BitmapMetadata;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 1; // メタデータが壊れていても、画像自体は表示したい
        }

        if (metadata is null)
        {
            return 1;
        }

        foreach (var query in new[] { "System.Photo.Orientation", fallbackQuery })
        {
            try
            {
                if (metadata.ContainsQuery(query) && metadata.GetQuery(query) is ushort value)
                {
                    return value is >= 1 and <= 8 ? value : 1;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // このクエリが使えない形式・壊れたメタデータ。次のクエリを試す
            }
        }

        return 1;
    }

    /// <summary>
    /// Orientation に従って回転・反転した画像を返す。
    /// TransformedBitmap は描画時に遅延計算されるので、CachedBitmap でここ（裏スレッド）で確定させる。
    /// </summary>
    internal static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),                                    // 左右反転
            3 => new RotateTransform(180),                                     // 180°
            4 => new ScaleTransform(1, -1),                                    // 上下反転
            5 => Combine(new RotateTransform(90), new ScaleTransform(-1, 1)),  // 右90° → 左右反転
            6 => new RotateTransform(90),                                      // 右90°
            7 => Combine(new RotateTransform(270), new ScaleTransform(-1, 1)), // 左90° → 左右反転
            8 => new RotateTransform(270),                                     // 左90°
            _ => null,
        };

        if (transform is null)
        {
            return source;
        }

        try
        {
            transform.Freeze();
            var transformed = new TransformedBitmap(source, transform);
            return new CachedBitmap(transformed, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 回せなかったら元の向きのまま出す
            return source;
        }
    }

    private static TransformGroup Combine(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var t in transforms)
        {
            group.Children.Add(t);
        }
        return group;
    }
}
