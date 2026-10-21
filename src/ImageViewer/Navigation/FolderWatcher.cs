using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace ImageViewer.Navigation;

/// <summary>
/// 開いているフォルダを見張って、画像が増えた・消えた・書き換わったら知らせる。
/// <list type="bullet">
/// <item>サブフォルダは見ない。対応している拡張子のファイルだけ見る</item>
/// <item>書き込みの途中で知らせないよう、変化が <c>quietPeriod</c> だけ止まってから1回だけ知らせる
/// （書き込みが続いても、最初の変化から <c>maxDelay</c> 経ったら知らせる）</item>
/// <item>見張れないフォルダ（ネットワークドライブの一部など）では、何も知らせないだけ</item>
/// </list>
/// <see cref="Changed"/> は作ったスレッド（UIスレッド）で起きる。
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _maxDelay;

    // 最初の変化からの時間（知らせたらリセット）
    private readonly Stopwatch _sinceFirstChange = new();

    private FileSystemWatcher? _watcher;

    public FolderWatcher(TimeSpan quietPeriod, TimeSpan maxDelay)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _maxDelay = maxDelay;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = quietPeriod };
        _timer.Tick += (_, _) => Raise();
    }

    /// <summary>フォルダの中身が変わった（変化が落ち着いた）とき</summary>
    public event EventHandler? Changed;

    /// <summary>見張っているフォルダ。見張っていなければ null</summary>
    public string? Directory { get; private set; }

    /// <summary>見張るフォルダを変える。null なら見張るのをやめる。同じフォルダなら何もしない</summary>
    public void Watch(string? directory)
    {
        if (string.Equals(directory, Directory, StringComparison.OrdinalIgnoreCase) && (directory is null || _watcher is not null))
        {
            return;
        }

        Stop();
        if (directory is null)
        {
            return;
        }

        Directory = directory;
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                // 書き込み中はサイズ・更新日時が変わり続けるので、落ち着くまで待てる
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 見張れないフォルダ。見張らないだけで、ほかはふつうに使える
            Debug.WriteLine($"フォルダを見張れません: {directory}: {ex.Message}");
        }
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        _timer.Stop();
        _sinceFirstChange.Reset();
        Directory = null;

        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    // ここから下の3つは FileSystemWatcher のスレッドで呼ばれる

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (FolderNavigator.IsSupported(e.FullPath))
        {
            Schedule(sender);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // 「一時ファイル → 画像」に名前を変えて書き出すソフトもあるので、どちらかが画像なら知らせる
        if (FolderNavigator.IsSupported(e.FullPath) || FolderNavigator.IsSupported(e.OldFullPath))
        {
            Schedule(sender);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 変化が多すぎて取りこぼした（バッファあふれ）。何が変わったか分からないので、とにかく知らせる
        Debug.WriteLine($"フォルダの見張りでエラー: {e.GetException().Message}");
        Schedule(sender);
    }

    private void Schedule(object sender)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _watcher)) return; // 前に見張っていたフォルダの知らせ

            if (!_sinceFirstChange.IsRunning)
            {
                _sinceFirstChange.Restart();
            }

            if (_sinceFirstChange.Elapsed >= _maxDelay)
            {
                Raise(); // 書き込みが続いていても、待たせすぎない
                return;
            }

            // 変化が止まるまで待ち直す
            _timer.Stop();
            _timer.Start();
        });
    }

    private void Raise()
    {
        _timer.Stop();
        _sinceFirstChange.Reset();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
