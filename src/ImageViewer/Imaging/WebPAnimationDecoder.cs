using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Interop;

namespace ImageViewer.Imaging;

/// <summary>
/// libwebpdemux.dll（WebPAnimDecoder）を使ったアニメーションWebP のデコーダ。
/// WebPAnimDecoder は合成済みのキャンバスを1コマずつ返してくれるので、GIF のような自前の合成はいらない。
/// アニメーションでない WebP は扱わない（WIC / libwebp に任せる）。
/// </summary>
public sealed class WebPAnimationDecoder : IImageDecoder
{
    // VP8X チャンクのフラグ（RIFF ヘッダ 12バイト + チャンク名 4バイト + サイズ 4バイト の次の1バイト）
    private const int Vp8xFlagsOffset = 20;
    private const byte AnimationFlag = 0x02;

    private readonly long _maxBytes;

    /// <param name="maxBytes">1枚のアニメーションに使っていいメモリ。超えたら1コマ目だけ表示する</param>
    public WebPAnimationDecoder(long maxBytes = AnimationLimits.DefaultMaxBytes)
    {
        _maxBytes = maxBytes;
    }

    public string Name => "libwebpdemux";

    public bool IsAvailable => LibWebPDemux.IsAvailable;

    public bool CanDecode(ReadOnlySpan<byte> header) => IsAnimatedWebP(header);

    /// <summary>"RIFF" .... "WEBP" "VP8X" で始まり、VP8X のアニメーションフラグが立っているか</summary>
    public static bool IsAnimatedWebP(ReadOnlySpan<byte> header) =>
        header.Length > Vp8xFlagsOffset
        && header[..4].SequenceEqual("RIFF"u8)
        && header.Slice(8, 4).SequenceEqual("WEBP"u8)
        && header.Slice(12, 4).SequenceEqual("VP8X"u8)
        && (header[Vp8xFlagsOffset] & AnimationFlag) != 0;

    // アニメーションは縮めない（メモリは AnimationMaxMB で抑える）
    public unsafe DecodedImage Decode(byte[] data, int maxPixelSize)
    {
        // デコーダは data をコピーせずに参照するので、Delete するまで fixed で止めておく
        fixed (byte* src = data)
        {
            var webpData = new LibWebPDemux.WebPData { Bytes = src, Size = (nuint)data.Length };
            var decoder = CreateDecoder(&webpData);
            try
            {
                return DecodeFrames(decoder);
            }
            finally
            {
                LibWebPDemux.WebPAnimDecoderDelete(decoder);
            }
        }
    }

    /// <summary>
    /// 1コマ目だけを BitmapSource（Freeze 済み）にする。サムネイル作成用。
    /// 全コマは読まないので、メモリ上限は関係ない。
    /// </summary>
    public static unsafe BitmapSource DecodeFirstFrame(byte[] data)
    {
        fixed (byte* src = data)
        {
            var webpData = new LibWebPDemux.WebPData { Bytes = src, Size = (nuint)data.Length };
            var decoder = CreateDecoder(&webpData);
            try
            {
                var (width, height, _) = ReadInfo(decoder);
                return ReadNextFrame(decoder, width, height, out _);
            }
            finally
            {
                LibWebPDemux.WebPAnimDecoderDelete(decoder);
            }
        }
    }

    /// <summary>デコーダを作る。使い終わったら WebPAnimDecoderDelete すること。</summary>
    private static unsafe IntPtr CreateDecoder(LibWebPDemux.WebPData* webpData)
    {
        LibWebPDemux.WebPAnimDecoderOptions options;
        if (LibWebPDemux.WebPAnimDecoderOptionsInitInternal(&options, LibWebPDemux.AbiVersion) == 0)
        {
            throw new NotSupportedException("libwebpdemux.dll のバージョンが合いません。");
        }
        options.ColorMode = LibWebPDemux.ModeBgra;
        options.UseThreads = 0;

        var decoder = LibWebPDemux.WebPAnimDecoderNewInternal(webpData, &options, LibWebPDemux.AbiVersion);
        if (decoder == IntPtr.Zero)
        {
            throw new NotSupportedException("アニメーションWebPを解析できませんでした。");
        }
        return decoder;
    }

    /// <summary>キャンバスの大きさとコマ数を読んで、扱える大きさか確かめる</summary>
    private static unsafe (int Width, int Height, LibWebPDemux.WebPAnimInfo Info) ReadInfo(IntPtr decoder)
    {
        LibWebPDemux.WebPAnimInfo info;
        if (LibWebPDemux.WebPAnimDecoderGetInfo(decoder, &info) == 0)
        {
            throw new NotSupportedException("アニメーションWebPの情報を読めませんでした。");
        }

        if (info.CanvasWidth == 0 || info.CanvasHeight == 0 || info.FrameCount == 0
            || info.CanvasWidth > int.MaxValue || info.CanvasHeight > int.MaxValue || info.FrameCount > int.MaxValue)
        {
            throw new NotSupportedException("アニメーションWebPの大きさが不正です。");
        }

        int width = (int)info.CanvasWidth;
        int height = (int)info.CanvasHeight;
        if (AnimationLimits.EstimateBytes(width, height, 1) > Array.MaxLength)
        {
            throw new NotSupportedException($"画像サイズが大きすぎます（{width}×{height}）。");
        }

        return (width, height, info);
    }

    /// <summary>次のコマを読んで BitmapSource（Freeze 済み）にする</summary>
    /// <param name="timestamp">このコマが終わる時刻（ミリ秒、累積）</param>
    private static unsafe BitmapSource ReadNextFrame(IntPtr decoder, int width, int height, out int timestamp)
    {
        byte* buffer;
        int ts;
        if (LibWebPDemux.WebPAnimDecoderGetNext(decoder, &buffer, &ts) == 0 || buffer == null)
        {
            throw new NotSupportedException("アニメーションWebPのデコードに失敗しました。");
        }
        timestamp = ts;

        // buffer は次の GetNext で上書きされるので、コピーしてから BitmapSource にする
        int stride = width * 4;
        long frameBytes = AnimationLimits.EstimateBytes(width, height, 1);
        var pixels = new byte[frameBytes];
        new ReadOnlySpan<byte>(buffer, (int)frameBytes).CopyTo(pixels);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private DecodedImage DecodeFrames(IntPtr decoder)
    {
        var (width, height, info) = ReadInfo(decoder);

        long required = AnimationLimits.EstimateBytes(width, height, (int)info.FrameCount);
        bool fits = required <= _maxBytes;

        var frames = new List<AnimationFrame>();
        int previousTimestamp = 0;
        while (LibWebPDemux.WebPAnimDecoderHasMoreFrames(decoder) != 0)
        {
            var bitmap = ReadNextFrame(decoder, width, height, out int timestamp);

            // timestamp は「このコマが終わる時刻」の累積なので、前との差が表示時間
            var delay = TimeSpan.FromMilliseconds(Math.Max(0, timestamp - previousTimestamp));
            previousTimestamp = timestamp;
            frames.Add(new AnimationFrame(bitmap, AnimationLimits.NormalizeDelay(delay)));

            if (!fits)
            {
                break; // 上限を超えるので1コマ目だけ
            }
        }

        if (frames.Count == 0)
        {
            throw new NotSupportedException("アニメーションWebPにコマがありません。");
        }

        if (!fits)
        {
            return new DecodedImage(frames[0].Image, AnimationLimits.TooLargeNotice(required, _maxBytes));
        }

        // WebP の loop_count は「再生する回数」（0 = 無限）なので、そのまま使える
        return new DecodedImage(frames, (int)Math.Min(info.LoopCount, int.MaxValue));
    }
}
