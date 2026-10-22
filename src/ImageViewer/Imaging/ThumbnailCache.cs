using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>ファイルが変わったかを見分けるための印（更新日時とサイズ）</summary>
public readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length)
{
    /// <summary>ファイルの印を取る。無い・読めないときは null</summary>
    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>サムネイル1枚の結果。読めなかったら Image は null</summary>
public sealed record ThumbnailResult(BitmapSource? Image)
{
    public static readonly ThumbnailResult Failed = new((BitmapSource?)null);

    public bool IsFailed => Image is null;
}

/// <summary>
/// サムネイルを裏で作って、メモリに持っておくキャッシュ。
/// <list type="bullet">
/// <item>同時に作るのは maxConcurrency 枚まで。待っている間にキャンセルされたものは作らない</item>
/// <item>合計が上限を超えたら、長く使っていないものから捨てる（LRU）</item>
/// <item>読めなかったことも覚えておく（スクロールのたびに読み直さない）</item>
/// <item><see cref="Invalidate"/> の後は、次に使うときにファイルの更新日時とサイズを見直し、変わっていたら作り直す</item>
/// </list>
/// ディスクには何も書かない。どのスレッドから呼んでもいい。
/// </summary>
public sealed class ThumbnailCache
{
    private sealed class Entry(string path, ThumbnailResult result, FileStamp? stamp, long bytes, int generation)
    {
        public string Path { get; } = path;
        public ThumbnailResult Result { get; } = result;
        public FileStamp? Stamp { get; } = stamp;
        public long Bytes { get; } = bytes;
        public int Generation { get; set; } = generation;
    }

    private readonly IThumbnailDecoder _decoder;
    private readonly Func<string, FileStamp?> _stampOf;
    private readonly SemaphoreSlim _gate;
    private readonly object _lock = new();

    // 先頭が最近使ったもの
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private int _generation;

    /// <param name="decoder">サムネイルを作る処理</param>
    /// <param name="pixelSize">作るサムネイルの長い辺（ピクセル）。表示の大きさを変えても作り直さないよう、最大の大きさで作る</param>
    /// <param name="maxBytes">持っておくサムネイルの合計の上限</param>
    /// <param name="maxConcurrency">同時に作る枚数</param>
    /// <param name="stampOf">ファイルの印の取り方（テストで差し替える）。省略時は <see cref="FileStamp.Of"/></param>
    public ThumbnailCache(
        IThumbnailDecoder decoder,
        int pixelSize,
        long maxBytes,
        int maxConcurrency = 2,
        Func<string, FileStamp?>? stampOf = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        _decoder = decoder;
        PixelSize = pixelSize;
        MaxBytes = Math.Max(0, maxBytes);
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _stampOf = stampOf ?? FileStamp.Of;
    }

    /// <summary>
    /// 同時に作る枚数の目安。CPU のコア数の半分（2〜4枚）。
    /// 少なすぎると一覧が埋まるのが遅く、多すぎると1枚表示に戻ったときのデコードや、ディスクの読み込みと取り合う。
    /// </summary>
    public static int ConcurrencyFor(int processorCount) => Math.Clamp(processorCount / 2, 2, 4);

    public int PixelSize { get; }

    public long MaxBytes { get; }

    /// <summary>今持っているサムネイルの合計（目安）</summary>
    public long CurrentBytes
    {
        get { lock (_lock) return _bytes; }
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// サムネイルを返す。持っていて見直しも要らなければ、完了済みの Task をすぐ返す（UIスレッドで待たずに使える）。
    /// 読めなかったときも例外にはせず、<see cref="ThumbnailResult.Failed"/> を返す。
    /// キャンセルされたら OperationCanceledException。
    /// </summary>
    public Task<ThumbnailResult> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        Entry? stale = null;
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var node))
            {
                if (node.Value.Generation == _generation)
                {
                    Touch(node);
                    return Task.FromResult(node.Value.Result);
                }
                stale = node.Value; // Invalidate 前に作ったもの。ファイルが変わっていなければそのまま使う
            }
        }

        return LoadAsync(path, stale, cancellationToken);
    }

    /// <summary>次に使うとき、ファイルが変わっていないか見直すようにする（フォルダを開き直したときなど）</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    private async Task<ThumbnailResult> LoadAsync(string path, Entry? stale, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Load(path, stale, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ThumbnailResult Load(string path, Entry? stale, CancellationToken cancellationToken)
    {
        int generation;
        lock (_lock)
        {
            generation = _generation;
        }

        var stamp = _stampOf(path);

        // ファイルが変わっていなければ、前に作ったものを使い続ける
        if (stale is not null && stamp is not null && stamp == stale.Stamp)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(path, out var node) && ReferenceEquals(node.Value, stale))
                {
                    stale.Generation = generation;
                    Touch(node);
                }
            }
            return stale.Result;
        }

        cancellationToken.ThrowIfCancellationRequested();

        ThumbnailResult result;
        try
        {
            result = new ThumbnailResult(_decoder.Decode(path, PixelSize));
        }
        catch (Exception ex)
        {
            // サムネイルが1枚作れなくても一覧は止めない（メモリ不足も含めて「読めなかった」扱い）
            Debug.WriteLine($"サムネイルを作れませんでした: {path}: {ex.Message}");
            result = ThumbnailResult.Failed;
        }

        Store(path, result, stamp, generation);
        return result;
    }

    private void Store(string path, ThumbnailResult result, FileStamp? stamp, int generation)
    {
        long bytes = result.Image is { } image
            ? (long)image.PixelWidth * image.PixelHeight * ((image.Format.BitsPerPixel + 7) / 8)
            : 0;

        lock (_lock)
        {
            if (_entries.Remove(path, out var old))
            {
                _lru.Remove(old);
                _bytes -= old.Value.Bytes;
            }

            var node = _lru.AddFirst(new Entry(path, result, stamp, bytes, generation));
            _entries[path] = node;
            _bytes += bytes;

            // 上限を超えたら古いものから捨てる。今入れた1枚は残す
            while (_bytes > MaxBytes && _lru.Last is { } last && last != node)
            {
                _lru.RemoveLast();
                _entries.Remove(last.Value.Path);
                _bytes -= last.Value.Bytes;
            }
        }
    }

    // _lock の中で呼ぶこと
    private void Touch(LinkedListNode<Entry> node)
    {
        if (_lru.First != node)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }
}
