using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Animation;
using ImageViewer.Dialogs;
using ImageViewer.Imaging;
using ImageViewer.Interop;
using ImageViewer.Metadata;
using ImageViewer.Navigation;
using ImageViewer.Settings;
using ImageViewer.Thumbnails;
using Microsoft.Win32;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private const string AppName = "ImageViewer";
    private const double MinZoom = 0.02;   // 2%
    private const double MaxZoom = 32.0;   // 3200%
    private const double ZoomStep = 1.25;

    private const string GlyphFullScreen = "\uE740";
    private const string GlyphBackToWindow = "\uE73F";
    private const string GlyphSortAscending = "\uE70E";   // ChevronUp
    private const string GlyphSortDescending = "\uE70D";  // ChevronDown
    private const string GlyphThumbnails = "\uE8A9";       // ViewAll
    private const string GlyphSingleImage = "\uE91B";      // Photo
    private const string GlyphPlay = "\uE768";             // Play
    private const string GlyphStop = "\uE71A";             // Stop

    // サムネイルを作る大きさの上限（物理ピクセル）。高DPIでも 256 DIP がぼやけないように DPI 倍するが、これ以上は大きくしない
    private const int MaxThumbnailPixelSize = 512;

    private const long BytesPerMB = 1024L * 1024;

    // 起動時に ImageViewer.ini から読み、終了時に書き戻す
    private readonly AppSettings _settings = AppSettings.Load(AppSettings.DefaultPath);

    // メモリ上限は設定から決まるので、コンストラクタで作る
    private readonly IImageLoader _loader;
    private readonly ImageCache _cache;

    // 縮めて読んだ画像を、元の大きさで読み直している（読み直した）ファイル。同じ画像で何度も読み直さないように
    private string? _fullResolutionPath;
    private readonly FolderNavigator _navigator = new();
    private readonly AnimationPlayer _player = new();

    // サムネイルのキャッシュ。作る大きさが画面の DPI で決まるので、最初に一覧を出すときに作る
    private ThumbnailCache? _thumbnailCache;

    // サムネイル一覧を表示中か
    private bool _thumbnailMode;

    // 一覧に入る前に表示していた画像（Esc で戻る先）。フォルダから一覧で開いたときなどは null
    private string? _pathBeforeThumbnails;

    // 情報パネル。起動時はいつも閉じている（開閉は保存しない）
    private bool _infoVisible;

    // 情報パネルに出している（読み込み中の）ファイル。同じファイルなら読み直さない
    private string? _infoPath;

    // 連続で選択を動かしたときに、古い読み込み結果を出さないための通し番号
    private int _infoVersion;

    // スライドショー中か
    private bool _slideshow;

    // 静止画を次へ送るタイマー（アニメーションは1周したところで送るので使わない）
    private readonly DispatcherTimer _slideTimer = new();

    // 表示中のアニメーションが1周するのを待っているか
    private bool _slideWaitingForAnimation;

    // 今の画像を出してからの時間（アニメーションを ini の時間まで繰り返すのに使う）
    private readonly Stopwatch _slideClock = new();

    // ファイルを開いて、フォルダの一覧を裏で作っている間は true（その間は一覧が1枚だけなので、前後の移動などはしない）
    private bool _listingPending;

    // ファイル・フォルダを開くたびに増やす。古い一覧の結果を捨てるのに使う
    private int _openVersion;

    // 隣のフォルダを探している・端のダイアログを出している間は true（重ねて動かさない）
    private bool _folderBusy;

    // フォルダの移動でたどる範囲の一番上。ファイル・フォルダを開いたときに、そのフォルダの親に決める
    // （隣のフォルダへ移っても変えない。ここより上には行かない）
    private string? _folderScopeRoot;

    // 端のダイアログを閉じてからの時間。ホイールを回し続けたときに、閉じた直後にまた出ないようにする
    private readonly Stopwatch _sinceEdgeDialog = new();

    // 画面下のお知らせを消すタイマー
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    // 直前の移動方向（+1: 次へ / -1: 前へ）。先読みする方向の優先度に使う
    private int _direction = 1;

    // 表示中の画像。アニメーションなら _bitmap は1コマ目（縦横ピクセル数・表示サイズの基準）
    private DecodedImage? _image;
    private BitmapSource? _bitmap;

    // _bitmap がどのファイルの画像か。読み込み中は Navigator の現在位置とずれる
    private string? _bitmapPath;

    // 表示状態。_zoom は「画像1ピクセル = 画面1ピクセル」を 1.0 とした倍率
    private bool _fitMode = true;
    private double _zoom = 1.0;
    private Vector _offset;   // 回転・拡縮後の画像の外接矩形の左上（Viewport座標）
    private int _rotation;    // 時計回りの角度（0 / 90 / 180 / 270）。表示のみで、ファイルには書き込まない

    // 全画面表示
    private bool _isFullScreen;
    private WindowState _restoreWindowState;

    // 並び順ボタンの表示をコードから合わせている最中は、Checked イベントを無視する
    private bool _updatingSortButtons;

    // 連打で古い読み込み結果が後から表示されないようにするための通し番号
    private int _loadVersion;

    // ドラッグでのパン
    private Point? _dragStart;
    private Vector _dragStartOffset;

    public MainWindow()
    {
        InitializeComponent();

        _loader = ImageLoader.CreateDefault(_settings.AnimationMaxMemoryMB * BytesPerMB);
        _cache = new ImageCache(_loader, _settings.PrefetchMaxMemoryMB * BytesPerMB);

        _player.Changed += AnimationPlayer_Changed;
        _player.PlayCompleted += AnimationPlayer_PlayCompleted;
        _slideTimer.Tick += SlideTimer_Tick;
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeHost.Visibility = Visibility.Collapsed;
        };

        ThumbnailView.ThumbnailSize = _settings.ThumbnailSize;

        // 前回の並び順を復元（まだ何も開いていないので、次に開いたフォルダから効く）
        _navigator.SetSort(_settings.SortKey, _settings.SortDescending);

        // 前回の位置がある場合は、画面中央に出す既定の配置をやめる（位置は OnSourceInitialized で戻す）
        if (_settings.Window is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        UpdateSortButtons();
        UpdateStatus();
        ShowNativeLibraryWarning();
    }

    /// <summary>
    /// WebP の DLL が読めるか調べて、読めなければステータスバーに出す（理由はツールチップ）。
    /// DLL が無くても WIC だけで動くので止めはしないが、黙っていると原因が分からないので。
    /// </summary>
    private void ShowNativeLibraryWarning()
    {
        (string Summary, string Detail)? problem;
        try
        {
            problem = NativeLibraryCheck.CheckWebP();
        }
        catch (Exception ex)
        {
            problem = ("WebP DLL の確認に失敗", ex.Message);
        }

        if (problem is not { } p) return;

        Debug.WriteLine($"{p.Summary}: {p.Detail}");
        NativeWarningText.Text = "⚠ " + p.Summary;
        NativeWarningItem.ToolTip = p.Detail;
        NativeWarningItem.Visibility = Visibility.Visible;
        NativeWarningSeparator.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------
    // 開く・移動
    // ------------------------------------------------------------

    /// <summary>ファイルまたはフォルダを開く</summary>
    public void OpenPath(string path)
    {
        bool isDirectory = Directory.Exists(path);
        bool opened;
        try
        {
            // ファイルは、まず1枚だけで開いてすぐ表示する。フォルダの一覧は裏で作る（LoadFolderListingAsync）
            opened = isDirectory
                ? _navigator.OpenDirectory(path)
                : _navigator.OpenSingleFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowMessage($"開けませんでした。\n{ex.Message}");
            return;
        }

        if (!opened)
        {
            ShowMessage($"表示できる画像が見つかりませんでした。\n{path}");
            return;
        }

        // 別のものを開いたら、スライドショーは終わり
        StopSlideshow();

        // 作りかけの一覧があれば、もう使わない
        _openVersion++;
        _listingPending = !isDirectory;

        // フォルダの移動は、開いたフォルダと同じ階層のフォルダ（とその中のサブフォルダ全部）をたどる
        _folderScopeRoot = _navigator.CurrentDirectory is { } openedFolder
            ? Path.GetDirectoryName(openedFolder) ?? openedFolder // ドライブのルートを開いたときは、その中だけ
            : null;

        // 開き直したときは、ファイルが更新されているかもしれないので読み直す
        _cache.Clear();
        _thumbnailCache?.Invalidate();
        _infoPath = null; // 情報も読み直す
        _direction = 1;

        if (isDirectory)
        {
            // フォルダは一覧から始める。前のフォルダの画像は片付けておく（一覧から戻ったときに出ないように）
            _loadVersion++;
            SetImage(null, null);
            HideMessage();
            _pathBeforeThumbnails = null;
            ShowThumbnails();
        }
        else
        {
            if (_thumbnailMode)
            {
                LeaveThumbnailMode();
            }

            // 画像のデコードを先に始めてから、一覧を作り始める
            _ = ShowCurrentAsync();
            _ = LoadFolderListingAsync(_navigator.Current!, _openVersion);
        }
    }

    /// <summary>
    /// 開いたファイルと同じフォルダの一覧を裏で作り、できたら入れ替える。
    /// ファイル数の多いフォルダやネットワークドライブでも、画像の表示を待たせないため。
    /// </summary>
    private async Task LoadFolderListingAsync(string filePath, int version)
    {
        var key = _navigator.SortKey;
        bool descending = _navigator.SortDescending;

        FolderListing? listing = null;
        try
        {
            listing = await Task.Run(() => FolderNavigator.ListFolderOf(filePath, key, descending));
        }
        catch (Exception ex)
        {
            // 投げっぱなしで呼んでいるので、ここで拾わないと誰にも見られずに消える。一覧は1枚だけのまま
            Debug.WriteLine(ex);
        }

        if (version != _openVersion)
        {
            return; // 作っている間に、別のファイル・フォルダが開かれた
        }

        _listingPending = false;
        if (listing is null || !_navigator.ApplyListing(listing))
        {
            ShowNotice("フォルダの画像一覧を作れませんでした");
        }
        else if (!_thumbnailMode && _navigator.Current is { } current)
        {
            UpdatePrefetch(current); // 一覧ができたので、前後の先読みを始める
        }

        UpdateStatus();
    }

    /// <param name="moved">位置が変わったか（FolderNavigator.MoveXxx の戻り値）</param>
    /// <param name="direction">+1: 次の方向へ / -1: 前の方向へ</param>
    private void Navigate(bool moved, int direction)
    {
        if (moved)
        {
            _direction = direction;
            _ = ShowCurrentAsync();
        }
    }

    /// <summary>
    /// 表示中の画像の前後を先読みし、それ以外をキャッシュから捨てる。
    /// 進んでいる方向を優先して2枚、逆方向を1枚（現在の画像と合わせて最大4枚をメモリに持つ）。
    /// 先読みの合計が上限（ini の PrefetchMaxMB）を超える分は、優先度の低いものから諦める。
    /// </summary>
    private void UpdatePrefetch(string currentPath)
    {
        _cache.MaxPixelSize = MonitorLongSide();

        var offsets = _direction >= 0 ? new[] { 1, 2, -1 } : new[] { -1, -2, 1 };
        var neighbors = offsets
            .Select(_navigator.PeekRelative)
            .OfType<string>()
            .ToList();

        // スライドショーでは最後の次は先頭に戻るので、最後の画像を出しているときは先頭も先読みする
        if (_slideshow && _direction >= 0 && _navigator.Index == _navigator.Count - 1
            && _navigator.Files is [var first, ..] && !neighbors.Contains(first, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(first, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            neighbors.Insert(0, first);
        }

        _cache.Retain(currentPath, neighbors);
        foreach (var path in neighbors)
        {
            _cache.Prefetch(path);
        }
    }

    private async Task ShowCurrentAsync()
    {
        int version = ++_loadVersion;
        var path = _navigator.Current;
        if (path is null)
        {
            SetImage(null, null);
            UpdateStatus();
            return;
        }

        UpdateStatus(); // ここではまだ前の画像が出ているので、縦横・倍率は「読み込み中」になる

        // スライドショーの次の送りは、この画像を出してから数え直す（手で送ったときも）
        CancelSlideWait();

        // 表示する画像の読み込みを先に始めてから、前後の先読みを予約する。
        // 画面よりずっと大きい画像は、画面の大きさに縮めて読む（拡大したら EnsureResolution で読み直す）
        _cache.MaxPixelSize = MonitorLongSide();
        var loadTask = _cache.GetAsync(path);
        UpdatePrefetch(path);

        try
        {
            var image = await loadTask;
            if (version != _loadVersion)
            {
                return; // もっと新しい画像の読み込みが始まっている
            }

            HideMessage();
            SetImage(image, path);
            ScheduleSlide();
        }
        catch (OperationCanceledException)
        {
            // 連打などで不要になって、キャッシュから捨てられた
            return;
        }
        catch (Exception ex)
        {
            // このメソッドは投げっぱなし（_ = ShowCurrentAsync()）で呼ぶので、
            // ここで拾わないと例外が誰にも見られずに消える。何が起きても画面には出す
            if (version != _loadVersion)
            {
                return;
            }

            SetImage(null, null);
            ShowMessage(ex switch
            {
                ImageLoadException => ex.Message,
                OutOfMemoryException => $"メモリが足りなくて表示できませんでした。\n{Path.GetFileName(path)}",
                _ => $"表示中にエラーが発生しました。\n{Path.GetFileName(path)}\n{ex.Message}",
            });

            if (ex is not ImageLoadException)
            {
                Debug.WriteLine(ex);
            }

            // 読めない画像でも止まらず、いつもの時間が経ったら次へ
            ScheduleSlide();
        }

        UpdateStatus();
    }

    private void ShowOpenDialog()
    {
        var extensions = string.Join(";", FolderNavigator.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "画像を開く",
            Filter = $"画像ファイル|{extensions}|すべてのファイル|*.*",
        };

        if (_navigator.Current is string current)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        }

        if (dialog.ShowDialog(this) == true)
        {
            OpenPath(dialog.FileName);
        }
    }

    // ------------------------------------------------------------
    // サムネイル一覧
    // ------------------------------------------------------------

    /// <summary>サムネイル一覧を出す。選択は今の画像にして、そこまでスクロールする</summary>
    private void ShowThumbnails()
    {
        if (_navigator.Count == 0 || _listingPending) return;

        StopSlideshow();

        if (!_thumbnailMode)
        {
            // Esc で戻れるように、今表示している画像を覚えておく
            _pathBeforeThumbnails = _bitmapPath;
        }

        // 読み込み途中の画像があれば、結果は捨てる（一覧の上にエラーなどを出さないため。戻ったときに読み直す）
        _loadVersion++;

        _thumbnailMode = true;
        ThumbnailView.Cache = EnsureThumbnailCache();
        ThumbnailView.Visibility = Visibility.Visible;
        Viewport.Visibility = Visibility.Hidden;
        HideMessage();
        ThumbnailView.SetFiles(_navigator.Files, _navigator.Index);

        UpdatePlayerSuspended();
        UpdateRotateButtons();
        UpdateStatus();
    }

    /// <summary>
    /// 一覧を閉じて1枚表示に戻る。
    /// </summary>
    /// <param name="backToPrevious">true なら一覧に入る前の画像に戻る（Esc）。false なら選択中の画像を開く</param>
    private void CloseThumbnails(bool backToPrevious)
    {
        if (!_thumbnailMode) return;

        if (backToPrevious)
        {
            int index = _pathBeforeThumbnails is { } previous ? IndexOfFile(previous) : -1;
            if (index < 0)
            {
                return; // 戻る画像が無い（フォルダを一覧で開いた直後など）。一覧のまま
            }
            _navigator.MoveToIndex(index);
        }

        LeaveThumbnailMode();

        if (_bitmap is not null && string.Equals(_bitmapPath, _navigator.Current, StringComparison.OrdinalIgnoreCase))
        {
            // 一覧に入る前と同じ画像。読み直さず、ズームや回転もそのまま
            UpdateStatus();
            UpdatePrefetch(_bitmapPath!);
        }
        else
        {
            _direction = 1;
            _ = ShowCurrentAsync();
        }
    }

    /// <summary>一覧を隠して画像表示領域に戻す（どの画像を出すかは呼び出し側で決める）</summary>
    private void LeaveThumbnailMode()
    {
        _thumbnailMode = false;
        ThumbnailView.Visibility = Visibility.Collapsed; // 隠れると、表示用に持っていたサムネイルは手放される
        Viewport.Visibility = Visibility.Visible;

        UpdatePlayerSuspended();
        UpdateRotateButtons();
    }

    private void ToggleThumbnails()
    {
        if (_thumbnailMode)
        {
            CloseThumbnails(backToPrevious: false);
        }
        else
        {
            ShowThumbnails();
        }
    }

    private ThumbnailCache EnsureThumbnailCache()
    {
        if (_thumbnailCache is null)
        {
            // 一番大きい表示（256 DIP）でもぼやけないよう、画面の DPI 倍の大きさで作る
            var dpi = VisualTreeHelper.GetDpi(this);
            double scale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
            int pixelSize = Math.Clamp(
                (int)Math.Ceiling(ThumbnailLayout.MaxSize * scale),
                ThumbnailLayout.MaxSize,
                MaxThumbnailPixelSize);

            _thumbnailCache = new ThumbnailCache(
                new ThumbnailDecoder(),
                pixelSize,
                _settings.ThumbnailMaxMemoryMB * BytesPerMB);
        }
        return _thumbnailCache;
    }

    private int IndexOfFile(string path)
    {
        var files = _navigator.Files;
        for (int i = 0; i < files.Count; i++)
        {
            if (string.Equals(files[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private void ThumbnailView_SelectionChanged(object? sender, int index)
    {
        // 選ぶだけでは画像を読まない（開いたときに読む）。ステータスバーのファイル名・何枚目かだけ合わせる
        _navigator.MoveToIndex(index);
        UpdateStatus();
    }

    private void ThumbnailView_ItemActivated(object? sender, int index)
    {
        _navigator.MoveToIndex(index);
        CloseThumbnails(backToPrevious: false);
    }

    private void ThumbnailView_ThumbnailSizeChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateRotateButtons()
    {
        bool enabled = _bitmap is not null && !_thumbnailMode;
        RotateLeftButton.IsEnabled = enabled;
        RotateRightButton.IsEnabled = enabled;
    }

    private void UpdateThumbnailButton()
    {
        ThumbnailButton.IsEnabled = _navigator.Count > 0 && !_listingPending;
        SlideshowButton.IsEnabled = _navigator.Count > 0 && !_listingPending;
        ExplorerButton.IsEnabled = _navigator.Count > 0;
        ThumbnailIcon.Text = _thumbnailMode ? GlyphSingleImage : GlyphThumbnails;
        ThumbnailButton.ToolTip = _thumbnailMode
            ? "選んだ画像を開く (T / Enter)"
            : "サムネイル一覧 (T)";
    }

    // ------------------------------------------------------------
    // フォルダの移動
    // ------------------------------------------------------------

    /// <summary>
    /// 次（前）の画像へ。フォルダの端なら、隣のフォルダへ行くか・同じフォルダの反対の端に戻るかをダイアログで聞く。
    /// </summary>
    /// <param name="direction">+1: 次へ / -1: 前へ</param>
    /// <param name="suppressDialog">端でもダイアログを出さない（キーの押しっぱなし・ホイールの回しすぎ）</param>
    private void GoNextOrAsk(int direction, bool suppressDialog)
    {
        // 一覧ができるまでは1枚だけなので、ここで送るとフォルダの端と間違えてダイアログが出てしまう
        if (_listingPending) return;

        bool moved = direction > 0 ? _navigator.MoveNext() : _navigator.MovePrevious();
        if (moved)
        {
            Navigate(true, direction);
            return;
        }

        // 押しっぱなしで端に来たときに、ダイアログが出た瞬間にボタンが押されてしまわないよう、押し直したときだけ聞く
        if (!suppressDialog && _navigator.Count > 0)
        {
            _ = AskAtFolderEdgeAsync(direction);
        }
    }

    private async Task AskAtFolderEdgeAsync(int direction)
    {
        if (_folderBusy || _navigator.CurrentDirectory is not { } folder) return;

        _folderBusy = true;
        try
        {
            string? sibling = await FindSiblingFolderAsync(folder, direction);
            if (!string.Equals(folder, _navigator.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return; // 探している間に別のフォルダが開かれた
            }

            bool stillAtEdge = direction > 0 ? _navigator.Index == _navigator.Count - 1 : _navigator.Index == 0;
            if (!stillAtEdge)
            {
                return; // 探している間に、手で別の画像へ移った
            }

            var choice = FolderEdgeDialog.Ask(this, direction, sibling is null ? null : FolderDisplayName(sibling));
            switch (choice)
            {
                case FolderEdgeChoice.MoveToFolder when sibling is not null:
                    // 次のフォルダは先頭から、前のフォルダは最後から
                    OpenSiblingFolder(sibling, selectLast: direction < 0);
                    break;

                case FolderEdgeChoice.Wrap:
                    Navigate(direction > 0 ? _navigator.MoveFirst() : _navigator.MoveLast(), direction);
                    break;
            }
        }
        finally
        {
            _folderBusy = false;
            _sinceEdgeDialog.Restart();
        }
    }

    /// <summary>↑↓（一覧では Ctrl+↑↓）で隣のフォルダへ。↓ は先頭の画像、↑ は最後の画像を出す</summary>
    private async Task MoveToSiblingFolderAsync(int direction)
    {
        if (_folderBusy || _navigator.CurrentDirectory is not { } folder) return;

        _folderBusy = true;
        try
        {
            string? sibling = await FindSiblingFolderAsync(folder, direction);
            if (!string.Equals(folder, _navigator.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (sibling is null)
            {
                ShowNotice(direction > 0 ? "次のフォルダはありません" : "前のフォルダはありません");
                return;
            }

            OpenSiblingFolder(sibling, selectLast: direction < 0);
        }
        finally
        {
            _folderBusy = false;
        }
    }

    /// <summary>隣のフォルダを裏で探す（ネットワークドライブなどで遅くても、画面を止めない）</summary>
    private async Task<string?> FindSiblingFolderAsync(string folder, int direction)
    {
        // 範囲が決まっていない・範囲の外にいる（通常は無い）ときは、今のフォルダの親を範囲にする
        string root = _folderScopeRoot ?? Path.GetDirectoryName(folder) ?? folder;
        try
        {
            return await Task.Run(() => FolderSiblings.Find(folder, root, direction));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>
    /// 隣のフォルダを開く。1枚表示ならその画像を出し、一覧なら一覧のまま中身を入れ替える。
    /// （OpenPath と違って、フォルダでも一覧には切り替えない）
    /// </summary>
    private void OpenSiblingFolder(string folder, bool selectLast)
    {
        bool opened;
        try
        {
            opened = _navigator.OpenDirectory(folder, selectLast);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice($"フォルダを開けませんでした（{ex.Message}）");
            return;
        }

        if (!opened)
        {
            ShowNotice($"表示できる画像がありませんでした：{Path.GetFileName(folder)}");
            return;
        }

        StopSlideshow();
        _openVersion++;          // 開いたファイルの一覧を作っている途中なら、もう使わない
        _listingPending = false; // OpenDirectory は一覧まで作り終えている
        _cache.Clear();
        _thumbnailCache?.Invalidate();
        _infoPath = null;
        _direction = selectLast ? -1 : +1;
        ShowNotice(FolderDisplayName(folder));

        if (_thumbnailMode)
        {
            _pathBeforeThumbnails = null; // 前のフォルダの画像には戻れない
            ThumbnailView.SetFiles(_navigator.Files, _navigator.Index);
            UpdateStatus();
        }
        else
        {
            _ = ShowCurrentAsync();
        }
    }

    /// <summary>範囲の一番上からの相対パス（サブフォルダに入ったときに、どこにいるか分かるように）</summary>
    private string FolderDisplayName(string folder) =>
        _folderScopeRoot is { } root ? FolderSiblings.DisplayName(folder, root) : Path.GetFileName(folder);

    /// <summary>画面下に数秒だけお知らせを出す</summary>
    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeHost.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ------------------------------------------------------------
    // スライドショー
    // ------------------------------------------------------------

    /// <summary>
    /// 今の画像からスライドショーを始める。一覧からなら、選択中の画像を開いて始める。
    /// 1枚の時間は ini の [Slideshow] IntervalSeconds（始めるときに読み直すので、起動中に書き換えても効く）。
    /// </summary>
    private void StartSlideshow()
    {
        if (_slideshow || _navigator.Count == 0 || _listingPending) return;

        _settings.SlideshowIntervalSeconds = AppSettings.Load(AppSettings.DefaultPath).SlideshowIntervalSeconds;
        _slideshow = true;
        UpdateSlideshowUi();

        if (_thumbnailMode)
        {
            // 一覧を閉じて選択中の画像を開く。出たところから数え始める（ShowCurrentAsync → ScheduleSlide）
            CloseThumbnails(backToPrevious: false);
            if (_slideshow && !_slideTimer.IsEnabled && !_slideWaitingForAnimation
                && _bitmap is not null && string.Equals(_bitmapPath, _navigator.Current, StringComparison.OrdinalIgnoreCase))
            {
                ScheduleSlide(); // 一覧に入る前と同じ画像で、読み直さなかった
            }
            return;
        }

        // 読み込み中なら、出たところで ScheduleSlide される。出ている（または読めなかった）なら、今から数える
        bool loading = _bitmap is not null && !string.Equals(_bitmapPath, _navigator.Current, StringComparison.OrdinalIgnoreCase);
        if (!loading)
        {
            ScheduleSlide();
        }
    }

    private void StopSlideshow()
    {
        if (!_slideshow) return;

        _slideshow = false;
        CancelSlideWait();
        UpdateSlideshowUi();
    }

    private void ToggleSlideshow()
    {
        if (_slideshow)
        {
            StopSlideshow();
        }
        else
        {
            StartSlideshow();
        }
    }

    private TimeSpan SlideInterval => TimeSpan.FromSeconds(_settings.SlideshowIntervalSeconds);

    /// <summary>
    /// 表示した画像の、次の送りを予約する。静止画は ini の時間で送る。
    /// アニメーションは、ini の時間が過ぎてから1周し終わったところで送る（短いものは時間になるまで繰り返す。長いものは1周で送る）
    /// </summary>
    private void ScheduleSlide()
    {
        if (!_slideshow) return;

        CancelSlideWait();
        if (_player.IsAnimated)
        {
            _slideWaitingForAnimation = true;
            _slideClock.Restart();
            if (_player.IsFinished)
            {
                _player.Restart(); // 再生し終わって止まっていたら、もう一度動かす
            }
        }
        else
        {
            _slideTimer.Interval = SlideInterval;
            _slideTimer.Start();
        }
    }

    private void CancelSlideWait()
    {
        _slideTimer.Stop();
        _slideClock.Reset();
        _slideWaitingForAnimation = false;
    }

    /// <summary>次の画像へ。最後の画像の次は先頭に戻る</summary>
    private void AdvanceSlide()
    {
        if (!_slideshow) return;

        CancelSlideWait();
        bool moved = _navigator.MoveNext() || _navigator.MoveFirst();
        if (moved)
        {
            Navigate(true, +1);
        }
        else
        {
            ScheduleSlide(); // 1枚しかない。そのまま出し続ける
        }
    }

    private void SlideTimer_Tick(object? sender, EventArgs e)
    {
        _slideTimer.Stop();

        // 最小化している間・フォルダの端のダイアログを出している間は送らない。待ち直す
        if (WindowState == WindowState.Minimized || _folderBusy)
        {
            ScheduleSlide();
            return;
        }

        AdvanceSlide();
    }

    private void AnimationPlayer_PlayCompleted(object? sender, EventArgs e)
    {
        if (!_slideshow || !_slideWaitingForAnimation || _folderBusy) return;

        if (_slideClock.Elapsed >= SlideInterval)
        {
            AdvanceSlide();
        }
        else if (_player.IsFinished)
        {
            // 再生回数が決まっているアニメーションが、時間になる前に止まった。時間になるまで繰り返す
            _player.Restart();
        }
        // 無限ループのものは、そのまま次の周へ
    }

    private void UpdateSlideshowUi()
    {
        SlideshowButton.IsEnabled = _navigator.Count > 0 && !_listingPending;
        SlideshowIcon.Text = _slideshow ? GlyphStop : GlyphPlay;
        SlideshowButton.ToolTip = _slideshow ? "スライドショーを止める (S / Esc)" : "スライドショー (S)";

        var visibility = _slideshow ? Visibility.Visible : Visibility.Collapsed;
        SlideshowItem.Visibility = visibility;
        SlideshowSeparator.Visibility = visibility;
        SlideshowText.Text = _slideshow
            ? string.Create(CultureInfo.InvariantCulture, $"スライドショー {_settings.SlideshowIntervalSeconds:0.###}秒")
            : string.Empty;
    }

    private void SlideshowButton_Click(object sender, RoutedEventArgs e) => ToggleSlideshow();

    // ------------------------------------------------------------
    // エクスプローラーで開く
    // ------------------------------------------------------------

    /// <summary>
    /// 表示中の画像（一覧なら選択中の画像）があるフォルダをエクスプローラーで開き、その画像を選んだ状態にする。
    /// ビューアでは編集・削除をしない方針なので、ファイルの操作はエクスプローラーに任せる。
    /// </summary>
    private void OpenInExplorer()
    {
        if (_navigator.Current is not { } path) return;

        if (!File.Exists(path))
        {
            // 消された・移動された。フォルダが残っていれば、フォルダだけ開く
            var folder = Path.GetDirectoryName(path);
            if (folder is null || !Directory.Exists(folder) || !Shell32.OpenFolderAndSelect(folder))
            {
                ShowNotice("ファイルが見つかりませんでした");
            }
            return;
        }

        if (Shell32.OpenFolderAndSelect(path))
        {
            return;
        }

        // シェルの関数が失敗したときの予備
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            ShowNotice("エクスプローラーを開けませんでした");
        }
    }

    private void ExplorerButton_Click(object sender, RoutedEventArgs e) => OpenInExplorer();

    // ------------------------------------------------------------
    // 情報パネル（EXIF）
    // ------------------------------------------------------------

    private void ToggleInfoPanel()
    {
        _infoVisible = !_infoVisible;
        var visibility = _infoVisible ? Visibility.Visible : Visibility.Collapsed;
        InfoView.Visibility = visibility;
        InfoSplitter.Visibility = visibility;
        InfoButton.IsChecked = _infoVisible;

        // 隠すときは列の幅も 0 にする（最小幅が残らないように）
        InfoColumn.MinWidth = _infoVisible ? AppSettings.MinInfoPanelWidth : 0;
        InfoColumn.MaxWidth = _infoVisible ? AppSettings.MaxInfoPanelWidth : double.PositiveInfinity;
        InfoColumn.Width = new GridLength(_infoVisible ? _settings.InfoPanelWidth : 0);

        if (_infoVisible)
        {
            _infoPath = null; // 閉じている間に変わっているかもしれないので読み直す
            UpdateInfoPanel();
        }
        else
        {
            _infoVersion++; // 読み込み中の結果は捨てる
        }
    }

    /// <summary>
    /// 情報パネルを今の画像（一覧なら選択中の画像）に合わせる。UpdateStatus から毎回呼ばれるので、
    /// 同じファイルなら何もしない。読むのは裏のスレッドで、画像のデコードはしない。
    /// </summary>
    private void UpdateInfoPanel()
    {
        if (!_infoVisible) return;

        var path = _navigator.Current;
        if (path is null)
        {
            _infoPath = null;
            _infoVersion++;
            InfoView.ShowMessage("画像が開かれていません");
            return;
        }

        if (string.Equals(path, _infoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _infoPath = path;
        _ = LoadInfoAsync(path, ++_infoVersion);
    }

    private async Task LoadInfoAsync(string path, int version)
    {
        ImageInfo info;
        try
        {
            info = await Task.Run(() => ImageInfoLoader.Load(path));
        }
        catch (Exception ex)
        {
            // 投げっぱなしで呼んでいるので、ここで拾わないと誰にも見られずに消える
            Debug.WriteLine(ex);
            if (version == _infoVersion)
            {
                InfoView.ShowMessage($"情報を読めませんでした。\n{Path.GetFileName(path)}");
            }
            return;
        }

        if (version == _infoVersion && _infoVisible)
        {
            InfoView.ShowInfo(info);
        }
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e) => ToggleInfoPanel();

    /// <summary>境目をドラッグし終わったら、今の幅を覚えておく（終了時に ini に保存する）</summary>
    private void InfoSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_infoVisible && InfoColumn.ActualWidth > 0)
        {
            _settings.InfoPanelWidth = AppSettings.ClampInfoPanelWidth((int)Math.Round(InfoColumn.ActualWidth));
        }
    }

    // ------------------------------------------------------------
    // 表示（ズーム・パン・回転）
    // ------------------------------------------------------------

    /// <summary>表示する画像を切り替える。アニメーションなら最初から自動再生する。</summary>
    private void SetImage(DecodedImage? image, string? path)
    {
        var bitmap = image?.Image;

        _image = image;
        _bitmap = bitmap;
        _bitmapPath = bitmap is null ? null : path;
        _fullResolutionPath = null;
        ImageView.Source = bitmap;
        _rotation = 0; // 回転は画像ごとにリセット

        // アニメーションのコマはどれもキャンバス全体の大きさなので、ズーム・回転はそのまま効く
        _player.Load(image);

        UpdateRotateButtons();

        if (bitmap is null)
        {
            ImageView.Width = 0;
            ImageView.Height = 0;
            return;
        }

        UpdateImageBaseSize();
        _fitMode = true;
        FitToWindow();
    }

    /// <summary>
    /// 等倍（1.0）・回転なしのときの Image のサイズを、DPIを考慮して設定する。
    /// WPFの単位はDIP（1/96インチ）なので、そのままだと150%表示環境で画像が拡大されてしまう。
    /// また画像ファイル自体のDPI（72dpiなど）も無視して、ピクセル数だけで決める。
    /// 縮めて読んだ画像でも元の大きさで決める（Stretch="Fill" で引き伸ばすので、ズームの計算は元の大きさのまま）。
    /// </summary>
    private void UpdateImageBaseSize()
    {
        if (_image is null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        ImageView.Width = _image.PixelWidth / dpi.DpiScaleX;
        ImageView.Height = _image.PixelHeight / dpi.DpiScaleY;
    }

    /// <summary>
    /// 画面に合わせて縮めて読む大きさ。今のウィンドウがあるモニタの長い辺（物理ピクセル）。
    /// ウィンドウに収めた表示はモニタより大きくならないので、これで足りる（拡大したら元の大きさで読み直す）。
    /// 取れなければ 0（縮めない）。
    /// </summary>
    private int MonitorLongSide()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return 0;

        var monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        var info = User32.MONITORINFO.Create();
        if (monitor == IntPtr.Zero || !User32.GetMonitorInfo(monitor, ref info)) return 0;

        var r = info.Monitor;
        return Math.Max(r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// 縮めて読んだ画像が、表示に足りなくなったら（拡大した・大きいモニタへ移った）元の大きさで読み直す。
    /// 読み直す間は縮めた画像を引き伸ばして出しておき、読めたら差し替える（ズーム・位置・回転はそのまま）。
    /// </summary>
    private void EnsureResolution()
    {
        if (_image is not { IsReduced: true } image || _bitmapPath is not { } path) return;
        if (string.Equals(_fullResolutionPath, path, StringComparison.OrdinalIgnoreCase)) return;

        // 画面に出ている大きさ（物理ピクセル。_zoom は元の画像の1ピクセル = 画面1ピクセルが 1.0）が、
        // 読んだ画像より大きくなったら読み直す
        double shown = Math.Max(image.PixelWidth, image.PixelHeight) * _zoom;
        double decoded = Math.Max(image.Image.PixelWidth, image.Image.PixelHeight);
        if (shown <= decoded + 1) return; // 丸めの誤差で読み直さないように、1ピクセルは見逃す

        _fullResolutionPath = path;
        _ = LoadFullResolutionAsync(path);
    }

    private async Task LoadFullResolutionAsync(string path)
    {
        DecodedImage full;
        try
        {
            full = await Task.Run(() => _loader.Load(path));
        }
        catch (Exception ex)
        {
            // 投げっぱなしで呼んでいるので、ここで拾う。縮めた画像のまま出し続ける
            Debug.WriteLine(ex);
            if (string.Equals(_bitmapPath, path, StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice(ex is OutOfMemoryException
                    ? "メモリが足りなくて、元の大きさで読み込めませんでした"
                    : "元の大きさで読み込めませんでした");
            }
            return;
        }

        // 読んでいる間に別の画像へ移っていたら捨てる
        if (_image is not { IsReduced: true } || !string.Equals(_bitmapPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // SetImage を通すとズーム・回転がリセットされるので、画像だけ差し替える（元の大きさは同じ）
        _image = full;
        _bitmap = full.Image;
        ImageView.Source = full.Image;
        UpdateStatus();
    }

    /// <summary>回転を反映した、等倍時の見た目のサイズ（90°/270°なら縦横が入れ替わる）</summary>
    private Size RotatedBaseSize =>
        _rotation % 180 == 0
            ? new Size(ImageView.Width, ImageView.Height)
            : new Size(ImageView.Height, ImageView.Width);

    /// <summary>ウィンドウに収まるように表示（小さい画像は拡大しない）</summary>
    private void FitToWindow()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (_bitmap is null || vw <= 0 || vh <= 0) return;

        var size = RotatedBaseSize;
        _zoom = Math.Min(1.0, Math.Min(vw / size.Width, vh / size.Height));
        _offset = new Vector((vw - size.Width * _zoom) / 2, (vh - size.Height * _zoom) / 2);
        ApplyTransform();
    }

    /// <summary>指定した点（Viewport座標）を中心にズームする</summary>
    private void ZoomAt(Point anchor, double newZoom)
    {
        if (_bitmap is null) return;

        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        double ratio = newZoom / _zoom;
        var a = (Vector)anchor;

        // anchor の下にある画像上の点が、ズーム後も同じ位置に来るようにオフセットを調整
        _offset = a - (a - _offset) * ratio;
        _zoom = newZoom;
        _fitMode = false;

        ClampOffset();
        ApplyTransform();
        UpdateStatus();
    }

    private Point ViewportCenter => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    private void ToggleFitMode(Point anchor)
    {
        if (_bitmap is null) return;

        if (_fitMode)
        {
            ZoomAt(anchor, 1.0);
        }
        else
        {
            _fitMode = true;
            FitToWindow();
            UpdateStatus();
        }
    }

    /// <summary>90°単位で回転する（正の値で時計回り）</summary>
    private void Rotate(int degrees)
    {
        if (_bitmap is null) return;

        if (_fitMode)
        {
            _rotation = NormalizeAngle(_rotation + degrees);
            FitToWindow();
        }
        else
        {
            // 画面中央に見えている画像上の点が、回転後も画面中央に来るようにする
            var center = ViewportCenter;
            var inverse = BuildMatrix();
            inverse.Invert();
            var imagePoint = inverse.Transform(center);

            _rotation = NormalizeAngle(_rotation + degrees);
            _offset = default;
            _offset = center - BuildMatrix().Transform(imagePoint);

            ClampOffset();
            ApplyTransform();
        }

        UpdateStatus();
    }

    private static int NormalizeAngle(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>
    /// 画像がウィンドウより小さい方向は中央寄せ、大きい方向は端に余白ができないよう制限する
    /// </summary>
    private void ClampOffset()
    {
        if (_bitmap is null) return;

        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        var size = RotatedBaseSize;
        double sw = size.Width * _zoom, sh = size.Height * _zoom;

        double x = sw <= vw ? (vw - sw) / 2 : Math.Clamp(_offset.X, vw - sw, 0);
        double y = sh <= vh ? (vh - sh) / 2 : Math.Clamp(_offset.Y, vh - sh, 0);
        _offset = new Vector(x, y);
    }

    /// <summary>
    /// Image（等倍・回転なしの矩形）→ 画面 への変換行列を作る。
    /// 1. 画像の中心を原点に移す → 2. 回転 → 3. 回転後の外接矩形の左上を原点に戻す
    /// → 4. 拡縮 → 5. オフセット分ずらす
    /// </summary>
    private Matrix BuildMatrix()
    {
        double w = ImageView.Width, h = ImageView.Height;
        var rotated = RotatedBaseSize;

        var m = Matrix.Identity;
        m.Translate(-w / 2, -h / 2);
        m.Rotate(_rotation);
        m.Translate(rotated.Width / 2, rotated.Height / 2);
        m.Scale(_zoom, _zoom);
        m.Translate(_offset.X, _offset.Y);
        return m;
    }

    private void ApplyTransform()
    {
        ImageView.RenderTransform = new MatrixTransform(BuildMatrix());

        // 200%以上はドットがくっきり見えるように補間なし、それ未満は高品質補間
        RenderOptions.SetBitmapScalingMode(
            ImageView,
            _zoom >= 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        EnsureResolution();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // 起動時の位置の復元で、DPI の違うモニタへ移ったとき。WPF が DPI の比率でサイズを変えてしまうので、
        // 保存したサイズ（物理ピクセル）をもう一度当て直す（同じモニタなので、もう DPI は変わらない）
        if (_restoringPlacement && _placementReapplyCount < 2)
        {
            _placementReapplyCount++;
            Dispatcher.InvokeAsync(ApplySavedPlacement, DispatcherPriority.Send);
        }

        if (_bitmap is null) return;

        UpdateImageBaseSize();
        if (_fitMode)
        {
            FitToWindow();
        }
        else
        {
            ClampOffset();
            ApplyTransform();
        }
    }

    // ------------------------------------------------------------
    // 並び順
    // ------------------------------------------------------------

    /// <summary>
    /// 並び順を変える。表示中の画像は読み直さず（ズームや回転もそのまま）、
    /// 一覧の順番と「何枚目か」、先読み対象だけを更新する。
    /// </summary>
    private void ApplySort(FileSortKey key, bool descending)
    {
        _navigator.SetSort(key, descending);
        UpdateSortButtons();

        if (_thumbnailMode)
        {
            // 一覧を並べ直す。選択中の画像はそのまま（位置だけ変わる）
            ThumbnailView.SetFiles(_navigator.Files, _navigator.Index);
        }
        else if (_navigator.Current is string current)
        {
            UpdatePrefetch(current);
        }

        UpdateStatus();
    }

    /// <summary>ツールバーの並び順ボタンを、現在の設定に合わせる</summary>
    private void UpdateSortButtons()
    {
        _updatingSortButtons = true;
        try
        {
            var key = _navigator.SortKey;
            SortByNameButton.IsChecked = key == FileSortKey.Name;
            SortByDateButton.IsChecked = key == FileSortKey.LastWriteTime;
            SortBySizeButton.IsChecked = key == FileSortKey.Size;

            bool descending = _navigator.SortDescending;
            SortDirectionIcon.Text = descending ? GlyphSortDescending : GlyphSortAscending;
            SortDirectionText.Text = descending ? "降順" : "昇順";
            SortDirectionButton.ToolTip = descending
                ? "降順で並べています（クリックで昇順に）"
                : "昇順で並べています（クリックで降順に）";
        }
        finally
        {
            _updatingSortButtons = false;
        }
    }

    // ------------------------------------------------------------
    // 設定の保存・復元
    // ------------------------------------------------------------

    /// <summary>
    /// ウィンドウのハンドルができた直後（まだ画面に出る前）に、前回の位置とサイズを戻す。
    /// ここで戻せば、既定の位置に一瞬出てから動く、ということがない。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestoreWindowPlacement();
    }

    // 位置の復元中（画面に出るまで）か。この間の DPI の変化では、保存したサイズを当て直す
    private bool _restoringPlacement;
    private int _placementReapplyCount;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _restoringPlacement = false; // 画面に出た後のモニタ間の移動は、ふつうに WPF に任せる
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            SaveSettings();
        }
    }

    /// <summary>
    /// 前回の位置とサイズを戻す。
    /// 保存したのは物理ピクセルなので、作られたときのモニタと DPI が違うモニタへ移すと、
    /// WPF が DPI の比率でサイズを変えてしまう（150% のモニタから 100% のモニタへ、など）。
    /// そこで2回当てる：1回目でそのモニタへ移り（ここで DPI が変わる）、2回目で正しいサイズにする。
    /// DPI の変化が後から届いた場合は、OnDpiChanged でもう一度当てる。
    /// </summary>
    private void RestoreWindowPlacement()
    {
        if (_settings.Window is null)
        {
            return;
        }

        _restoringPlacement = true;
        ApplySavedPlacement();
        ApplySavedPlacement();

        if (_settings.Window.Maximized)
        {
            // Show の前に設定しておけば、上で決めた位置のモニタで最大化して出る
            WindowState = WindowState.Maximized;
        }
    }

    private void ApplySavedPlacement()
    {
        if (_settings.Window is not { } bounds)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var placement = User32.WINDOWPLACEMENT.Create();
        placement.ShowCmd = User32.SW_HIDE; // 位置だけ決める。表示は WPF の Show に任せる
        placement.NormalPosition = new User32.RECT
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Left + bounds.Width,
            Bottom = bounds.Top + bounds.Height,
        };

        // 保存した位置が画面外になっていても（モニタを外した等）、OS が画面内に収めてくれる。
        // ShowCmd は、もう画面に出ていれば今の状態を保つ（最大化中に当て直しても最大化が解けないように）。
        // まだ出ていなければ SW_HIDE のまま（表示は WPF の Show に任せる）
        if (User32.IsWindowVisible(hwnd) && User32.GetWindowPlacement(hwnd, ref placement))
        {
            placement.NormalPosition = new User32.RECT
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Left + bounds.Width,
                Bottom = bounds.Top + bounds.Height,
            };
        }

        if (!User32.SetWindowPlacement(hwnd, ref placement))
        {
            Debug.WriteLine("ウィンドウの位置を戻せませんでした");
        }
    }

    /// <summary>今のウィンドウ位置と並び順を ImageViewer.ini に書く。書けなくても終了は止めない。</summary>
    private void SaveSettings()
    {
        _settings.SortKey = _navigator.SortKey;
        _settings.SortDescending = _navigator.SortDescending;
        _settings.ThumbnailSize = ThumbnailView.ThumbnailSize;

        // メモリ上限は手で編集する項目なので、起動中に ini を直されていたらそちらを残す（上書きで消さない）
        var onDisk = AppSettings.Load(AppSettings.DefaultPath);
        _settings.AnimationMaxMemoryMB = onDisk.AnimationMaxMemoryMB;
        _settings.PrefetchMaxMemoryMB = onDisk.PrefetchMaxMemoryMB;
        _settings.ThumbnailMaxMemoryMB = onDisk.ThumbnailMaxMemoryMB;
        _settings.SlideshowIntervalSeconds = onDisk.SlideshowIntervalSeconds;

        var hwnd = new WindowInteropHelper(this).Handle;
        var placement = User32.WINDOWPLACEMENT.Create();
        if (hwnd != IntPtr.Zero && User32.GetWindowPlacement(hwnd, ref placement))
        {
            // 最大化・全画面・最小化中でも、NormalPosition は「元に戻したときの位置とサイズ」
            var r = placement.NormalPosition;

            bool maximized = _isFullScreen
                ? _restoreWindowState == WindowState.Maximized // 全画面にする前の状態
                : WindowState switch
                {
                    WindowState.Maximized => true,
                    WindowState.Minimized => (placement.Flags & User32.WPF_RESTORETOMAXIMIZED) != 0,
                    _ => false,
                };

            _settings.Window = new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, maximized);
        }

        if (!_settings.TrySave(AppSettings.DefaultPath))
        {
            Debug.WriteLine($"設定を保存できませんでした: {AppSettings.DefaultPath}");
        }
    }

    // ------------------------------------------------------------
    // 全画面表示
    // ------------------------------------------------------------

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        if (_isFullScreen) return;

        _restoreWindowState = WindowState;
        _isFullScreen = true;

        ToolBarHost.Visibility = Visibility.Collapsed;
        StatusBarHost.Visibility = Visibility.Collapsed;

        // 最大化中に枠だけ消してもタスクバーが隠れないので、一度 Normal に戻してから最大化し直す
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        FullScreenIcon.Text = GlyphBackToWindow;
        FullScreenButton.ToolTip = "全画面表示を終了 (Enter / Esc)";
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen) return;

        _isFullScreen = false;

        // 一度 Normal に戻すことで、全画面前のウィンドウ位置・サイズ（RestoreBounds）に戻る
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = _restoreWindowState;

        ToolBarHost.Visibility = Visibility.Visible;
        StatusBarHost.Visibility = Visibility.Visible;

        FullScreenIcon.Text = GlyphFullScreen;
        FullScreenButton.ToolTip = "全画面表示 (Enter)";
    }

    // ------------------------------------------------------------
    // アニメーション
    // ------------------------------------------------------------

    /// <summary>コマが進んだ・一時停止した、など。表示中のコマとステータスバーを合わせる</summary>
    private void AnimationPlayer_Changed(object? sender, EventArgs e)
    {
        if (_player.CurrentFrame is { } frame)
        {
            // SetImage を通すと回転・ズームがリセットされるので、Source だけ差し替える
            ImageView.Source = frame;
        }
        UpdateStatus();
    }

    /// <summary>最小化している間は再生を止めておく（戻したら続きから）</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdatePlayerSuspended();
    }

    /// <summary>最小化中・一覧を表示中は、アニメーションを止めておく（見えないので）</summary>
    private void UpdatePlayerSuspended()
    {
        _player.Suspended = WindowState == WindowState.Minimized || _thumbnailMode;
    }

    protected override void OnClosed(EventArgs e)
    {
        _player.Load(null); // タイマーを止める
        base.OnClosed(e);
    }

    // ------------------------------------------------------------
    // ステータスバー・タイトル・メッセージ
    // ------------------------------------------------------------

    private void UpdateStatus()
    {
        UpdateThumbnailButton();
        UpdateInfoPanel();

        var path = _navigator.Current;

        if (path is null)
        {
            Title = AppName;
            FileNameText.Text = string.Empty;
            IndexText.Text = string.Empty;
            PixelSizeText.Text = string.Empty;
            ZoomText.Text = string.Empty;
            SetFrameText(null);
            return;
        }

        var fileName = Path.GetFileName(path);

        // タスクバーで見分けられるよう、タイトルにはファイル名だけ残す
        Title = $"{fileName} - {AppName}";

        FileNameText.Text = fileName;
        FileNameText.ToolTip = path;
        // 一覧を作っている間は、何枚目かがまだ分からない
        IndexText.Text = _listingPending ? "… / …" : $"{_navigator.Index + 1} / {_navigator.Count}";

        if (_thumbnailMode)
        {
            // 一覧では画像を読んでいないので縦横は出さない。倍率の欄にサムネイルの大きさを出す
            PixelSizeText.Text = string.Empty;
            ZoomText.Text = $"{ThumbnailView.ThumbnailSize}px";
            SetFrameText(null);
        }
        else if (_bitmap is null)
        {
            // 何も出ていない（読み込み失敗など）
            PixelSizeText.Text = string.Empty;
            ZoomText.Text = string.Empty;
            SetFrameText(null);
        }
        else if (!string.Equals(_bitmapPath, path, StringComparison.OrdinalIgnoreCase))
        {
            // 前の画像を出したまま、次の画像を読み込み中。前の画像の縦横を新しいファイル名と並べない
            PixelSizeText.Text = "読み込み中…";
            ZoomText.Text = string.Empty;
            SetFrameText(null);
        }
        else
        {
            // 縮めて読んでいても、元の画像の大きさを出す
            PixelSizeText.Text = $"{_image!.PixelWidth} × {_image.PixelHeight}";
            ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
            SetFrameText(BuildFrameText());
        }
    }

    /// <summary>ステータスバーのコマ表示（「コマ 3 / 20」など）。アニメーションでも補足もなければ null</summary>
    private string? BuildFrameText()
    {
        if (_player.IsAnimated)
        {
            var state = _player.IsFinished ? "（再生終了）"
                : _player.IsPaused ? "（一時停止）"
                : string.Empty;
            return $"コマ {_player.FrameIndex + 1} / {_player.FrameCount}{state}";
        }

        // 大きすぎて1コマ目だけにした、など
        return _image?.Notice;
    }

    /// <summary>コマ表示の欄を出す / 隠す（静止画では欄ごと隠す）</summary>
    private void SetFrameText(string? text)
    {
        var visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        FrameText.Text = text ?? string.Empty;
        FrameItem.Visibility = visibility;
        FrameSeparator.Visibility = visibility;
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageText.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------
    // ツールバー
    // ------------------------------------------------------------

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => Rotate(-90);

    private void RotateRightButton_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ThumbnailButton_Click(object sender, RoutedEventArgs e) => ToggleThumbnails();

    private void SortKeyButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingSortButtons) return;

        if (sender is FrameworkElement { Tag: FileSortKey key })
        {
            ApplySort(key, _navigator.SortDescending);
        }
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e) =>
        ApplySort(_navigator.SortKey, !_navigator.SortDescending);

    // ------------------------------------------------------------
    // 入力イベント
    // ------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (_thumbnailMode)
        {
            HandleThumbnailKey(e, ctrl);
            return;
        }

        switch (e.Key)
        {
            case Key.O when ctrl:
                ShowOpenDialog();
                break;

            case Key.T:
                if (!e.IsRepeat)
                {
                    ShowThumbnails();
                }
                break;

            case Key.I:
                if (!e.IsRepeat)
                {
                    ToggleInfoPanel();
                }
                break;

            case Key.S:
                if (!e.IsRepeat)
                {
                    ToggleSlideshow();
                }
                break;

            case Key.E:
                if (!e.IsRepeat)
                {
                    OpenInExplorer();
                }
                break;

            // Esc はスライドショーを先に止める（全画面はそのまま。もう一度 Esc で全画面も終わる）
            case Key.Escape when _slideshow:
                StopSlideshow();
                break;

            case Key.Enter:
                // 押しっぱなしで全画面が点滅しないように、キーリピートは無視
                if (!e.IsRepeat)
                {
                    ToggleFullScreen();
                }
                break;

            case Key.Escape when _isFullScreen:
                ExitFullScreen();
                break;

            case Key.R:
                Rotate(90);
                break;

            case Key.L:
                Rotate(-90);
                break;

            // アニメーション：P で一時停止 ⇔ 再生、, . でコマ送り（コマ送りすると一時停止になる）
            case Key.P when _player.IsAnimated:
                _player.TogglePause();
                break;

            case Key.OemComma when _player.IsAnimated:
                _player.Step(-1);
                break;

            case Key.OemPeriod when _player.IsAnimated:
                _player.Step(+1);
                break;

            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                GoNextOrAsk(+1, e.IsRepeat);
                break;

            case Key.Left:
            case Key.PageUp:
            case Key.Back:
                GoNextOrAsk(-1, e.IsRepeat);
                break;

            // ↓ で次のフォルダの先頭、↑ で前のフォルダの最後
            case Key.Down:
                _ = MoveToSiblingFolderAsync(+1);
                break;

            case Key.Up:
                _ = MoveToSiblingFolderAsync(-1);
                break;

            case Key.Home:
                Navigate(_navigator.MoveFirst(), +1);
                break;

            case Key.End:
                Navigate(_navigator.MoveLast(), -1);
                break;

            case Key.F:
                ToggleFitMode(ViewportCenter);
                break;

            case Key.D1:
            case Key.NumPad1:
                ZoomAt(ViewportCenter, 1.0);
                break;

            case Key.Add:
            case Key.OemPlus:
                ZoomAt(ViewportCenter, _zoom * ZoomStep);
                break;

            case Key.Subtract:
            case Key.OemMinus:
                ZoomAt(ViewportCenter, _zoom / ZoomStep);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>サムネイル一覧を表示中のキー操作</summary>
    private void HandleThumbnailKey(KeyEventArgs e, bool ctrl)
    {
        switch (e.Key)
        {
            case Key.O when ctrl:
                ShowOpenDialog();
                break;

            // 一覧では Enter は全画面ではなく「開く」（全画面はツールバーのボタンで）
            case Key.Enter:
            case Key.T:
                if (!e.IsRepeat)
                {
                    CloseThumbnails(backToPrevious: false);
                }
                break;

            case Key.I:
                if (!e.IsRepeat)
                {
                    ToggleInfoPanel();
                }
                break;

            // 一覧の ↑↓ は選択の移動なので、フォルダの移動は Ctrl+↑↓
            case Key.Down when ctrl:
                _ = MoveToSiblingFolderAsync(+1);
                break;

            case Key.Up when ctrl:
                _ = MoveToSiblingFolderAsync(-1);
                break;

            // 選択中の画像を開いて、そこからスライドショー
            case Key.S:
                if (!e.IsRepeat)
                {
                    StartSlideshow();
                }
                break;

            // 選択中の画像を、エクスプローラーで選んだ状態で開く
            case Key.E:
                if (!e.IsRepeat)
                {
                    OpenInExplorer();
                }
                break;

            case Key.Escape when _isFullScreen:
                ExitFullScreen();
                break;

            case Key.Escape:
                CloseThumbnails(backToPrevious: true);
                break;

            default:
                // 矢印・PageUp/PageDown・Home/End・Space/BackSpace で選択を動かす
                if (!ThumbnailView.HandleKey(e.Key))
                {
                    return;
                }
                break;
        }

        e.Handled = true;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double factor = Math.Pow(ZoomStep, e.Delta / 120.0);
            ZoomAt(e.GetPosition(Viewport), _zoom * factor);
        }
        else
        {
            // ホイールは連続で来るので、端のダイアログを閉じた直後は出し直さない
            bool justClosed = _sinceEdgeDialog.IsRunning && _sinceEdgeDialog.Elapsed < TimeSpan.FromSeconds(1);
            GoNextOrAsk(e.Delta > 0 ? -1 : +1, suppressDialog: justClosed);
        }

        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap is null) return;

        var position = e.GetPosition(Viewport);

        if (e.ClickCount == 2)
        {
            ToggleFitMode(position);
            e.Handled = true;
            return;
        }

        _dragStart = position;
        _dragStartOffset = _offset;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || !Viewport.IsMouseCaptured) return;

        _offset = _dragStartOffset + (e.GetPosition(Viewport) - start);
        ClampOffset();
        ApplyTransform();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Viewport.IsMouseCaptured)
        {
            Viewport.ReleaseMouseCapture(); // → LostMouseCapture で後片付け
        }
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragStart = null;
        Viewport.Cursor = null;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_bitmap is null) return;

        if (_fitMode)
        {
            FitToWindow();
            UpdateStatus();
        }
        else
        {
            ClampOffset();
            ApplyTransform();
        }
    }

    private void Viewport_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Viewport_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            OpenPath(paths[0]);
            Activate();
        }
    }
}
