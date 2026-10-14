namespace ImageViewer.Imaging;

/// <summary>アニメーションのデコードで共通に使う決めごと</summary>
public static class AnimationLimits
{
    /// <summary>1枚のアニメーションに使っていいメモリの既定値（256MB）</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>これ以下の表示時間は「指定なし」とみなす（ブラウザと同じ扱い）</summary>
    private static readonly TimeSpan TooShortDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>表示時間が短すぎるときに代わりに使う時間</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 表示時間を補正する。0 や 10ms 以下のコマはブラウザに合わせて 100ms にする
    /// （そうしないと、ブラウザでは普通に見えるのに爆速で回るファイルがけっこうある）。
    /// </summary>
    public static TimeSpan NormalizeDelay(TimeSpan delay) =>
        delay <= TooShortDelay ? DefaultDelay : delay;

    /// <summary>アニメーション全体を合成したときのメモリ量（キャンバス × コマ数、BGRA 4バイト/ピクセル）</summary>
    public static long EstimateBytes(int canvasWidth, int canvasHeight, int frameCount) =>
        (long)canvasWidth * canvasHeight * 4 * frameCount;

    /// <summary>上限を超えて1枚目だけにしたときの、ステータスバー用の説明</summary>
    public static string TooLargeNotice(long requiredBytes, long maxBytes) =>
        $"アニメ省略（{ToMB(requiredBytes)}MB > 上限 {ToMB(maxBytes)}MB）";

    private static long ToMB(long bytes) => (bytes + 1024 * 1024 - 1) / (1024 * 1024);
}
