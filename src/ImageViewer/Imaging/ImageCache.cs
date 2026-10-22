namespace ImageViewer.Imaging;

/// <summary>
/// デコード済み画像のキャッシュと先読み。
/// <para>
/// ・表示する画像は <see cref="GetAsync"/> ですぐにデコードを始める（先読みより優先）。<br/>
/// ・先読みは <see cref="Prefetch"/> で予約し、裏で1枚ずつ順番にデコードする。<br/>
/// ・<see cref="Retain"/> で指定したもの以外はキャッシュから捨てる（メモリを使いすぎないため）。<br/>
/// ・先読みした画像の合計が上限（prefetchBudgetBytes）を超えないようにする。表示中の画像は上限に数えない。
/// </para>
/// スレッド安全ではない。すべてのメソッドはUIスレッドから呼ぶこと。
/// </summary>
public sealed class ImageCache
{
    private readonly IImageLoader _loader;
    private readonly long _prefetchBudgetBytes;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // 一度デコードしたことのある画像の大きさ。上限で捨てた画像を、すぐまた先読みし直さないために覚えておく
    private readonly Dictionary<string, long> _knownSizes = new(StringComparer.OrdinalIgnoreCase);

    // 表示中の画像（先読みの上限に数えない）
    private string? _current;

    // 先読みは同時に1枚まで。表示中の画像のデコードとCPUを取り合わないようにする
    private readonly SemaphoreSlim _prefetchGate = new(1, 1);

    /// <summary>
    /// 長い辺がこれを大きく超える静止画は、ここまで縮めて読む（0 なら元の大きさ）。画面の大きさを入れる。
    /// これから読むものに効く（読み終わっているものは読み直さない）。
    /// </summary>
    public int MaxPixelSize { get; set; }

    /// <param name="prefetchBudgetBytes">先読みに使っていいメモリの合計。表示中の画像は含まない</param>
    public ImageCache(IImageLoader loader, long prefetchBudgetBytes = long.MaxValue)
    {
        _loader = loader;
        _prefetchBudgetBytes = prefetchBudgetBytes;
    }

    /// <summary>
    /// 画像を取得する。キャッシュ済み・読み込み中ならそれを返し、なければすぐに読み込みを始める。
    /// 失敗したら <see cref="ImageLoadException"/> を投げる。
    /// </summary>
    public Task<DecodedImage> GetAsync(string path)
    {
        if (_entries.TryGetValue(path, out var entry))
        {
            if (entry.Task.IsFaulted || entry.Task.IsCanceled)
            {
                // 前回失敗したもの（コピー中だった等）や、止められたものは読み直す
                Remove(path);
            }
            else if (entry.IsPrefetch && !entry.HasStarted)
            {
                // 先読みの順番待ちのまま表示対象になったら、割り込んで今すぐ読む
                Remove(path);
            }
            else
            {
                // 読み込み済み、または読み込み中（先読みで始まっていたものも含む）
                return entry.Task;
            }
        }

        return Start(path, prefetch: false).Task;
    }

    /// <summary>
    /// アニメーションの1コマ目を、全コマができる前に受け取る（<see cref="GetAsync"/> の後に呼ぶ）。
    /// 1コマ目ができた時点で、その静止画を返す。静止画・読み終わった後・キャッシュに無いときは null を返す。
    /// 先読みで読み始めていたものでも受け取れる。失敗やキャンセルでも例外にはならない。
    /// </summary>
    public Task<DecodedImage?> GetFirstFrameAsync(string path) =>
        _entries.TryGetValue(path, out var entry) && !entry.Task.IsCompleted
            ? entry.FirstFrame
            : Task.FromResult<DecodedImage?>(null);

    /// <summary>
    /// 先読みを予約する。すでにキャッシュにあれば何もしない。
    /// 先読み済みの合計が上限に達している（またはこの画像を足すと超えると分かっている）ときも何もしない。
    /// </summary>
    public void Prefetch(string path)
    {
        if (_entries.ContainsKey(path))
        {
            return;
        }

        long used = PrefetchedBytes();
        long expected = _knownSizes.GetValueOrDefault(path);
        if (used >= _prefetchBudgetBytes || used + expected > _prefetchBudgetBytes)
        {
            return;
        }

        Start(path, prefetch: true);
    }

    /// <summary>
    /// 表示中の画像と、先読みしたい画像（優先順）以外をキャッシュから捨てる。順番待ちの先読みはキャンセルされる。
    /// 先読み済みの合計が上限を超えていたら、優先度の低いものから捨てる。
    /// </summary>
    /// <param name="current">表示中の画像</param>
    /// <param name="neighbors">先読みしたい画像。優先度の高い順</param>
    public void Retain(string current, IReadOnlyList<string> neighbors)
    {
        _current = current;

        // 捨てる前に、読み終わっているものの大きさを覚えておく
        foreach (var (path, entry) in _entries)
        {
            if (SizeOf(entry) is long size and > 0)
            {
                _knownSizes[path] = size;
            }
        }

        var keep = new HashSet<string>(neighbors, StringComparer.OrdinalIgnoreCase) { current };
        foreach (var path in _entries.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            Remove(path);
        }
        foreach (var path in _knownSizes.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            _knownSizes.Remove(path);
        }

        // 上限を超えた分は、優先度の低いものから捨てる
        long used = 0;
        foreach (var path in neighbors)
        {
            if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)
                || !_entries.TryGetValue(path, out var entry))
            {
                continue;
            }

            long size = SizeOf(entry);
            if (used + size > _prefetchBudgetBytes)
            {
                Remove(path);
            }
            else
            {
                used += size;
            }
        }
    }

    /// <summary>キャッシュを全部捨てる（覚えている大きさも。ファイルが更新されているかもしれないので）</summary>
    public void Clear()
    {
        foreach (var path in _entries.Keys.ToList())
        {
            Remove(path);
        }
        _knownSizes.Clear();
        _current = null;
    }

    /// <summary>先読み済み（表示中以外）の画像のメモリ合計。読み込み中のものは 0 として数える</summary>
    private long PrefetchedBytes() =>
        _entries
            .Where(kv => !string.Equals(kv.Key, _current, StringComparison.OrdinalIgnoreCase))
            .Sum(kv => SizeOf(kv.Value));

    private static long SizeOf(Entry entry) =>
        entry.Task.IsCompletedSuccessfully ? entry.Task.Result?.ByteSize ?? 0 : 0;

    private Entry Start(string path, bool prefetch)
    {
        var entry = new Entry(prefetch);
        var token = entry.Cancellation.Token;
        int maxPixelSize = MaxPixelSize; // 裏のスレッドから読まないように、ここで決めておく

        if (!prefetch)
        {
            // 表示用：ゲートを通さずにすぐ読む
            entry.Task = Task.Run(() =>
            {
                entry.MarkStarted();
                return _loader.Load(path, maxPixelSize, entry.SetFirstFrame);
            });
        }
        else
        {
            // 先読み：ゲートで1枚ずつ。順番待ちの間にキャンセルされたら読まずに終わる
            entry.Task = Task.Run(async () =>
            {
                await _prefetchGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    entry.MarkStarted();
                    return _loader.Load(path, maxPixelSize, entry.SetFirstFrame);
                }
                finally
                {
                    _prefetchGate.Release();
                }
            }, token);
        }

        // 読み終わったら（失敗・キャンセルでも）、1コマ目を待っている側を待たせたままにしない
        entry.Task.ContinueWith(_ => entry.SetFirstFrame(null), TaskScheduler.Default);

        _entries[path] = entry;
        return entry;
    }

    private void Remove(string path)
    {
        if (_entries.Remove(path, out var entry))
        {
            // デコード中のものは止められない（終わり次第GCに回収される）。順番待ちのものだけ止まる。
            // CancellationTokenSource はタイマーを使っていないので Dispose しなくても漏れない。
            entry.Cancellation.Cancel();
        }
    }

    private sealed class Entry(bool isPrefetch)
    {
        private int _started;

        public bool IsPrefetch { get; } = isPrefetch;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<DecodedImage> Task { get; set; } = null!;

        // アニメーションの1コマ目（全コマができる前に先に見せる）。静止画・読み終わった後は null で終わる
        private readonly TaskCompletionSource<DecodedImage?> _firstFrame =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DecodedImage?> FirstFrame => _firstFrame.Task;

        /// <summary>1コマ目ができた（デコーダから、裏のスレッドで呼ばれる）。最初の1回だけ効く</summary>
        public void SetFirstFrame(DecodedImage? image) => _firstFrame.TrySetResult(image);

        /// <summary>デコードが始まったか（バックグラウンドで書き込み、UIスレッドで読む）</summary>
        public bool HasStarted => Volatile.Read(ref _started) != 0;

        public void MarkStarted() => Volatile.Write(ref _started, 1);
    }
}
