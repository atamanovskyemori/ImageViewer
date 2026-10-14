using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// WPF標準（Windows Imaging Component）によるデコーダ。
/// JPEG/PNG/GIF/BMP/TIFF/ICO/JPEG XR は標準で読める。
/// WebP/HEIC などは、OSにコーデック（拡張機能）が入っていれば読める。
/// JPEG/TIFF は EXIF の向き（Orientation）を見て、正しい向きに直してから返す（表示用。ファイルは変えない）。
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    // EXIF の Orientation タグ（274）。System.Photo.Orientation で取れなかったときの予備
    private const string JpegOrientationQuery = "/app1/ifd/{ushort=274}";
    private const string TiffOrientationQuery = "/ifd/{ushort=274}";

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

        BitmapFrame frame = decoder.Frames[0];
        BitmapSource result = frame;

        // WICのJPEG/TIFFデコーダはEXIFの向きを反映しないので自分で回す。
        // ほかの形式（HEICなど）はコーデック側で反映済みのことがあるので、二重に回さないよう対象を絞る
        string? fallbackQuery = decoder switch
        {
            JpegBitmapDecoder => JpegOrientationQuery,
            TiffBitmapDecoder => TiffOrientationQuery,
            _ => null,
        };
        if (fallbackQuery is not null)
        {
            result = ApplyOrientation(frame, ReadOrientation(frame, fallbackQuery));
        }

        if (result.CanFreeze)
        {
            result.Freeze();
        }
        return result;
    }

    /// <summary>EXIF の Orientation（1〜8）を読む。無い・読めないときは 1（そのまま）</summary>
    private static int ReadOrientation(BitmapFrame frame, string fallbackQuery)
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
    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
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
