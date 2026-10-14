namespace ImageViewer.Imaging;

/// <summary>GIF の「描画後の後始末」（Graphic Control Extension の Disposal Method）</summary>
public enum GifDisposal
{
    /// <summary>指定なし（残す）</summary>
    None = 0,

    /// <summary>残す</summary>
    DoNotDispose = 1,

    /// <summary>そのコマの範囲を背景に戻す（ブラウザに合わせて透明にする）</summary>
    RestoreBackground = 2,

    /// <summary>そのコマを描く前の状態に戻す</summary>
    RestorePrevious = 3,
}

/// <summary>
/// GIF のコマを1枚のキャンバスに順番に重ねて、各コマの「見た目」を作る。
/// GIF のコマは前の画面からの差分（キャンバスの一部だけ・透明あり）なので、そのまま並べると崩れる。
/// <para>
/// ピクセルはすべて BGRA（1ピクセル4バイト、非乗算済みアルファ）。WPF に依存しないのでテストしやすい。
/// </para>
/// </summary>
public sealed class GifFrameComposer
{
    private byte[] _canvas;

    public GifFrameComposer(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"キャンバスの大きさが不正です（{width}×{height}）。");
        }

        long size = (long)width * height * 4;
        if (size > Array.MaxLength)
        {
            throw new NotSupportedException($"キャンバスが大きすぎます（{width}×{height}）。");
        }

        Width = width;
        Height = height;
        _canvas = new byte[size]; // 最初は全部透明
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>1行のバイト数</summary>
    public int Stride => Width * 4;

    /// <summary>
    /// コマを1枚重ねて、そのコマを表示したときのキャンバス全体（コピー）を返す。
    /// 返したあとで Disposal に従ってキャンバスを片付け、次のコマに備える。
    /// </summary>
    /// <param name="framePixels">コマのピクセル（BGRA、width×height）。アルファ0のピクセルは下が透けて見える</param>
    /// <param name="left">キャンバス上のコマの左端。はみ出した部分は切り捨てる</param>
    /// <param name="top">キャンバス上のコマの上端</param>
    public byte[] Compose(ReadOnlySpan<byte> framePixels, int left, int top, int width, int height, GifDisposal disposal)
    {
        if (width < 0 || height < 0 || framePixels.Length < (long)width * height * 4)
        {
            throw new ArgumentException("コマのピクセル数が大きさと合いません。", nameof(framePixels));
        }

        // 「前の状態に戻す」なら、描く前に取っておく
        byte[]? saved = disposal == GifDisposal.RestorePrevious ? (byte[])_canvas.Clone() : null;

        // キャンバスに収まる範囲（はみ出しは切り捨て）
        int x0 = Math.Max(left, 0);
        int y0 = Math.Max(top, 0);
        int x1 = (int)Math.Min((long)left + width, Width);
        int y1 = (int)Math.Min((long)top + height, Height);

        for (int y = y0; y < y1; y++)
        {
            int src = (((y - top) * width) + (x0 - left)) * 4;
            int dst = ((y * Width) + x0) * 4;
            for (int x = x0; x < x1; x++, src += 4, dst += 4)
            {
                // GIF の透明は「完全に透明」だけなので、アルファ0なら下を残し、それ以外は上書き
                if (framePixels[src + 3] != 0)
                {
                    _canvas[dst] = framePixels[src];
                    _canvas[dst + 1] = framePixels[src + 1];
                    _canvas[dst + 2] = framePixels[src + 2];
                    _canvas[dst + 3] = framePixels[src + 3];
                }
            }
        }

        var snapshot = (byte[])_canvas.Clone();

        switch (disposal)
        {
            case GifDisposal.RestoreBackground when x1 > x0:
                for (int y = y0; y < y1; y++)
                {
                    int dst = ((y * Width) + x0) * 4;
                    Array.Clear(_canvas, dst, (x1 - x0) * 4);
                }
                break;

            case GifDisposal.RestorePrevious:
                _canvas = saved!;
                break;
        }

        return snapshot;
    }
}
