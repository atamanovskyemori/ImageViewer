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
    /// <param name="originalWidth">縮めて読んだときの、元の幅（向きを直した後）。0 なら image のまま</param>
    /// <param name="originalHeight">縮めて読んだときの、元の高さ（向きを直した後）。0 なら image のまま</param>
    public DecodedImage(BitmapSource image, string? notice = null, int originalWidth = 0, int originalHeight = 0)
    {
        Image = image;
        Notice = notice;
        PixelWidth = originalWidth > 0 ? originalWidth : image.PixelWidth;
        PixelHeight = originalHeight > 0 ? originalHeight : image.PixelHeight;
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
        PixelWidth = Image.PixelWidth;
        PixelHeight = Image.PixelHeight;
        if (frames.Count > 1)
        {
            Frames = frames;
            LoopCount = Math.Max(0, loopCount);
        }
        ByteSize = frames.Sum(f => SizeOf(f.Image));
    }

    /// <summary>表示用の1枚（アニメーションなら1コマ目）。縮めて読んだときは元より小さい</summary>
    public BitmapSource Image { get; }

    /// <summary>元の画像の幅（ピクセル。向きを直した後）。縮めて読んでも元の大きさ</summary>
    public int PixelWidth { get; }

    /// <summary>元の画像の高さ（ピクセル。向きを直した後）。縮めて読んでも元の大きさ</summary>
    public int PixelHeight { get; }

    /// <summary>画面に合わせて縮めて読んだか（拡大したら、元の大きさで読み直す）</summary>
    public bool IsReduced => Image.PixelWidth < PixelWidth || Image.PixelHeight < PixelHeight;

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
