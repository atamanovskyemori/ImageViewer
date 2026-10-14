using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// WIC（GifBitmapDecoder）で読んだ GIF のコマを、メタデータに従って合成してアニメーションにする。
/// メタデータが欠けていたり壊れていたりしても、なるべく既定値で表示を続ける。
/// </summary>
public static class GifAnimation
{
    /// <summary>
    /// アニメーションGIFをデコードする。
    /// 全コマを合成したときのメモリが maxBytes を超えるなら、1コマ目だけの静止画にする。
    /// </summary>
    public static DecodedImage Decode(BitmapDecoder decoder, long maxBytes)
    {
        var frames = decoder.Frames;
        var infos = frames.Select(ReadFrameInfo).ToList();

        // キャンバス（論理画面）の大きさ。無ければコマが収まる大きさにする
        var global = TryGetMetadata(() => decoder.Metadata);
        int canvasWidth = ReadInt(global, "/logscrdesc/Width") ?? 0;
        int canvasHeight = ReadInt(global, "/logscrdesc/Height") ?? 0;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            canvasWidth = infos.Max(i => i.Left + i.Width);
            canvasHeight = infos.Max(i => i.Top + i.Height);
        }

        // キャンバス1枚すら持てないほど大きい（壊れた論理画面サイズなど）なら、合成せずに1コマ目をそのまま出す
        long oneCanvas = AnimationLimits.EstimateBytes(canvasWidth, canvasHeight, 1);
        if (oneCanvas > Math.Min(Array.MaxLength, maxBytes))
        {
            BitmapSource first = frames[0];
            if (first.CanFreeze)
            {
                first.Freeze();
            }
            return new DecodedImage(first, AnimationLimits.TooLargeNotice(oneCanvas * frames.Count, maxBytes));
        }

        long required = AnimationLimits.EstimateBytes(canvasWidth, canvasHeight, frames.Count);
        bool fits = required <= maxBytes;
        int count = fits ? frames.Count : 1;

        var composer = new GifFrameComposer(canvasWidth, canvasHeight);
        var result = new List<AnimationFrame>(count);
        for (int i = 0; i < count; i++)
        {
            var info = infos[i];
            var pixels = ToBgra(frames[i]);
            var canvas = composer.Compose(pixels, info.Left, info.Top, info.Width, info.Height, info.Disposal);

            var bitmap = BitmapSource.Create(
                canvasWidth, canvasHeight, 96, 96, PixelFormats.Bgra32, null, canvas, composer.Stride);
            bitmap.Freeze();
            result.Add(new AnimationFrame(bitmap, AnimationLimits.NormalizeDelay(info.Delay)));
        }

        if (!fits)
        {
            return new DecodedImage(result[0].Image, AnimationLimits.TooLargeNotice(required, maxBytes));
        }

        var application = ReadBytes(global, "/appext/Application");
        var data = ReadBytes(global, "/appext/Data");
        return new DecodedImage(result, ParseLoopCount(application, data));
    }

    /// <summary>
    /// NETSCAPE2.0 拡張からループ回数（再生する総回数。0 = 無限）を読む。
    /// <list type="bullet">
    /// <item>拡張が無い → 1回だけ再生（ブラウザと同じ）</item>
    /// <item>値が 0 → 無限ループ</item>
    /// <item>値が N → 「最初の1回 + N回くり返し」で N+1 回（Chrome / Firefox と同じ解釈）</item>
    /// </list>
    /// </summary>
    /// <param name="application">"/appext/Application" の値（"NETSCAPE2.0" など）</param>
    /// <param name="data">"/appext/Data" の値。WIC はサブブロックの長さ付きで返す：{ 3, 1, 下位, 上位, 0 }</param>
    public static int ParseLoopCount(byte[]? application, byte[]? data)
    {
        if (application is null || data is null)
        {
            return 1;
        }

        var name = Encoding.ASCII.GetString(application);
        if (name is not ("NETSCAPE2.0" or "ANIMEXTS1.0"))
        {
            return 1;
        }

        if (data.Length < 4 || data[0] < 3 || data[1] != 1)
        {
            return 1;
        }

        int repeat = data[2] | (data[3] << 8);
        return repeat == 0 ? 0 : repeat + 1;
    }

    /// <summary>GIF の Delay（1/100秒単位）を時間にする（補正前）</summary>
    public static TimeSpan DelayFromCentiseconds(int centiseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, centiseconds) * 10.0);

    private sealed record FrameInfo(int Left, int Top, int Width, int Height, TimeSpan Delay, GifDisposal Disposal);

    private static FrameInfo ReadFrameInfo(BitmapFrame frame)
    {
        var metadata = TryGetMetadata(() => frame.Metadata);

        int disposal = ReadInt(metadata, "/grctlext/Disposal") ?? 0;
        return new FrameInfo(
            Left: ReadInt(metadata, "/imgdesc/Left") ?? 0,
            Top: ReadInt(metadata, "/imgdesc/Top") ?? 0,
            // 大きさはメタデータより実際のピクセル数を信じる（ピクセルの取り出しに使うので）
            Width: frame.PixelWidth,
            Height: frame.PixelHeight,
            Delay: DelayFromCentiseconds(ReadInt(metadata, "/grctlext/Delay") ?? 0),
            Disposal: disposal is >= 0 and <= 3 ? (GifDisposal)disposal : GifDisposal.None);
    }

    /// <summary>
    /// コマを BGRA のピクセル配列にする。
    /// GIF のコマはパレット形式（Indexed8）で、透明色はパレット上でアルファ0になっているので、Bgra32 に変換すればそのまま使える。
    /// </summary>
    private static byte[] ToBgra(BitmapSource frame)
    {
        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int stride = frame.PixelWidth * 4;
        var pixels = new byte[(int)stride * frame.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BitmapMetadata? TryGetMetadata(Func<ImageMetadata?> getter)
    {
        try
        {
            return getter() as BitmapMetadata;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // メタデータを持たない・壊れている。既定値で続ける
        }
    }

    private static object? Query(BitmapMetadata? metadata, string query)
    {
        if (metadata is null)
        {
            return null;
        }

        try
        {
            return metadata.ContainsQuery(query) ? metadata.GetQuery(query) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // 壊れたメタデータは「無い」扱い
        }
    }

    private static int? ReadInt(BitmapMetadata? metadata, string query) =>
        Query(metadata, query) switch
        {
            byte b => b,
            ushort us => us,
            short s => s,
            uint ui when ui <= int.MaxValue => (int)ui,
            int i => i,
            _ => null,
        };

    private static byte[]? ReadBytes(BitmapMetadata? metadata, string query) =>
        Query(metadata, query) as byte[];
}
