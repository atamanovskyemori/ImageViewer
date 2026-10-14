using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// デコード済み画像のキャッシュと先読み。
/// <para>
/// ・表示する画像は <see cref="GetAsync"/> ですぐにデコードを始める（先読みより優先）。<br/>
/// ・先読みは <see cref="Prefetch"/> で予約し、裏で1枚ずつ順番にデコードする。<br/>
/// ・<see cref="Retain"/> で指定したもの以外はキャッシュから捨てる（メモリを使いすぎないため）。
/// </para>
/// スレッド安全ではない。すべてのメソッドはUIスレッドから呼ぶこと。
/// </summary>
public sealed class ImageCache
{
    private readonly ImageLoader _loader;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // 先読みは同時に1枚まで。表示中の画像のデコードとCPUを取り合わないようにする
    private readonly SemaphoreSlim _prefetchGate = new(1, 1);

    public ImageCache(ImageLoader loader)
    {
        _loader = loader;
    }

    /// <summary>
    /// 画像を取得する。キャッシュ済み・読み込み中ならそれを返し、なければすぐに読み込みを始める。
    /// 失敗したら <see cref="ImageLoadException"/> を投げる。
    /// </summary>
    public Task<BitmapSource> GetAsync(string path)
    {
        if (_entries.TryGetValue(path, out var entry))
        {
            if (entry.Task.IsFaulted)
            {
                // 前回失敗したもの（コピー中だった等）は読み直す
                Remove(path);
            }
            else if (entry.IsPrefetch && !entry.HasStarted)
            {
                // 先読みの順番待ちのまま表示対象になったら、割り込んで今すぐ読む
                Remove(path);
            }
            else
            {
                return entry.Task;
            }
        }

        return Start(path, prefetch: false).Task;
    }

    /// <summary>先読みを予約する。すでにキャッシュにあれば何もしない。</summary>
    public void Prefetch(string path)
    {
        if (!_entries.TryGetValue(path, out _))
        {
            Start(path, prefetch: true);
        }
    }

    /// <summary>指定したパス以外をキャッシュから捨てる。順番待ちの先読みはキャンセルされる。</summary>
    public void Retain(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _entries.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            Remove(path);
        }
    }

    /// <summary>キャッシュを全部捨てる</summary>
    public void Clear()
    {
        foreach (var path in _entries.Keys.ToList())
        {
            Remove(path);
        }
    }

    private Entry Start(string path, bool prefetch)
    {
        var entry = new Entry(prefetch);
        var token = entry.Cancellation.Token;

        if (!prefetch)
        {
            entry.Task = Task.Run(() =>
            {
                entry.MarkStarted();
                return _loader.Load(path);
            });
        }
        else
        {
            entry.Task = Task.Run(async () =>
            {
                // prefetch=true の経路のみ async ラムダを使う（必要な await を含められる）
                return _loader.Load(path);
            });
        }

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

        public Task<BitmapSource> Task { get; set; } = null!;

        /// <summary>デコードが始まったか（バックグラウンドで書き込み、UIスレッドで読む）</summary>
        public bool HasStarted => Volatile.Read(ref _started) != 0;

        public void MarkStarted() => Volatile.Write(ref _started, 1);
    }
}
