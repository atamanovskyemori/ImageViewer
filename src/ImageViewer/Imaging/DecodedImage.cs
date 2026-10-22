using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>アニメーションの1コマ。Image はキャンバス全体（合成済み）。</summary>
public sealed record AnimationFrame(BitmapSource Image, TimeSpan Delay);

/// <summary>
/// デコード結果。静止画なら Animation は null、アニメーションなら合成済みのコマを全部持つか、
/// メモリの上限を超えるものは再生しながら作る（<see cref="Animation"/>）。
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
            Animation = new FrameList(frames);
            LoopCount = Math.Max(0, loopCount);
        }
        ByteSize = frames.Sum(f => SizeOf(f.Image));
    }

    /// <summary>
    /// 再生しながらコマを作るアニメーション（全コマを持つとメモリの上限を超えるもの。<see cref="StreamingFrames"/>）
    /// </summary>
    /// <param name="firstFrame">1コマ目（縦横ピクセル数・コマを作るまでの表示に使う）</param>
    /// <param name="byteSize">持っておくメモリのおおよその量（元のファイル + 持っておくコマの分）</param>
    /// <param name="notice">ステータスバーに出す補足（「省メモリ再生」など）</param>
    public DecodedImage(BitmapSource firstFrame, StreamingFrames streaming, int loopCount, long byteSize, string? notice)
    {
        Image = firstFrame;
        PixelWidth = firstFrame.PixelWidth;
        PixelHeight = firstFrame.PixelHeight;
        Animation = streaming;
        LoopCount = Math.Max(0, loopCount);
        ByteSize = byteSize;
        Notice = notice;
    }

    /// <summary>表示用の1枚（アニメーションなら1コマ目）。縮めて読んだときは元より小さい</summary>
    public BitmapSource Image { get; }

    /// <summary>元の画像の幅（ピクセル。向きを直した後）。縮めて読んでも元の大きさ</summary>
    public int PixelWidth { get; }

    /// <summary>元の画像の高さ（ピクセル。向きを直した後）。縮めて読んでも元の大きさ</summary>
    public int PixelHeight { get; }

    /// <summary>画面に合わせて縮めて読んだか（拡大したら、元の大きさで読み直す）</summary>
    public bool IsReduced => Image.PixelWidth < PixelWidth || Image.PixelHeight < PixelHeight;

    /// <summary>全コマを合成済みで持っているアニメーションのコマ（2枚以上）。静止画・再生しながら作るものは null</summary>
    public IReadOnlyList<AnimationFrame>? Frames { get; }

    /// <summary>アニメーションのコマの出どころ（全コマ持っているもの・再生しながら作るもの）。静止画なら null</summary>
    public IAnimationFrames? Animation { get; }

    public bool IsAnimated => Animation is not null;

    /// <summary>再生する回数。0 なら無限ループ（静止画では意味なし）</summary>
    public int LoopCount { get; }

    /// <summary>ステータスバーに出す補足。無ければ null</summary>
    public string? Notice { get; }

    /// <summary>ピクセルデータのおおよそのメモリ量（キャッシュの上限管理に使う）</summary>
    public long ByteSize { get; }

    private static long SizeOf(BitmapSource bitmap) =>
        (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);
}
