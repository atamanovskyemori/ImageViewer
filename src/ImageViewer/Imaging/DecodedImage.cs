using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>アニメーションの1コマ。Image はキャンバス全体（合成済み）。</summary>
public sealed record AnimationFrame(BitmapSource Image, TimeSpan Delay);

/// <summary>
/// デコード結果。静止画なら Frames は null、アニメーションなら合成済みのコマを全部持つ。
/// 中の BitmapSource はすべて Freeze 済み（どのスレッドからでも読める）。
/// </summary>
public sealed class DecodedImage
{
    /// <summary>静止画（またはアニメーションを1枚目だけにしたもの）</summary>
    /// <param name="notice">ステータスバーに出す補足（「大きすぎるので1枚目だけ」など）。無ければ null</param>
    public DecodedImage(BitmapSource image, string? notice = null)
    {
        Image = image;
        Notice = notice;
        ByteSize = SizeOf(image);
    }

    /// <summary>アニメーション。コマが1枚だけなら静止画として扱う。</summary>
    /// <param name="loopCount">再生する回数。0 なら無限ループ</param>
    public DecodedImage(IReadOnlyList<AnimationFrame> frames, int loopCount)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("フレームが1枚もありません。", nameof(frames));
        }

        Image = frames[0].Image;
        if (frames.Count > 1)
        {
            Frames = frames;
            LoopCount = Math.Max(0, loopCount);
        }
        ByteSize = frames.Sum(f => SizeOf(f.Image));
    }

    /// <summary>表示用の1枚（アニメーションなら1コマ目）。縦横ピクセル数もこれで見る</summary>
    public BitmapSource Image { get; }

    /// <summary>アニメーションのコマ（2枚以上）。静止画なら null</summary>
    public IReadOnlyList<AnimationFrame>? Frames { get; }

    public bool IsAnimated => Frames is not null;

    /// <summary>再生する回数。0 なら無限ループ（静止画では意味なし）</summary>
    public int LoopCount { get; }

    /// <summary>ステータスバーに出す補足。無ければ null</summary>
    public string? Notice { get; }

    /// <summary>ピクセルデータのおおよそのメモリ量（キャッシュの上限管理に使う）</summary>
    public long ByteSize { get; }

    private static long SizeOf(BitmapSource bitmap) =>
        (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);
}
