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
    private readonly ImageCache _cache;
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

        _cache = new ImageCache(
            ImageLoader.CreateDefault(_settings.AnimationMaxMemoryMB * BytesPerMB),
            _settings.PrefetchMaxMemoryMB * BytesPerMB);

        _player.Changed += AnimationPlayer_Changed;
        _player.PlayCompleted += AnimationPlayer_PlayCompleted;
        _slideTimer.Tick += SlideTimer_Tick;

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
    }

    // ------------------------------------------------------------
    // 開く・移動
    // ------------------------------------------------------------

    /// <summary>ファイルまたはフォルダを開く</summary>
    public void OpenPath(string path)
    {
        bool opened;
        try
        {
            opened = Directory.Exists(path)
                ? _navigator.OpenDirectory(path)
                : _navigator.OpenFile(path);
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

        // 開き直したときは、ファイルが更新されているかもしれないので読み直す
        _cache.Clear();
        _thumbnailCache?.Invalidate();
        _infoPath = null; // 情報も読み直す
        _direction = 1;

        if (Directory.Exists(path))
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
            _ = ShowCurrentAsync();
        }
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

        // 表示する画像の読み込みを先に始めてから、前後の先読みを予約する
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
        if (_navigator.Count == 0) return;

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
        ThumbnailButton.IsEnabled = _navigator.Count > 0;
        SlideshowButton.IsEnabled = _navigator.Count > 0;
        ThumbnailIcon.Text = _thumbnailMode ? GlyphSingleImage : GlyphThumbnails;
        ThumbnailButton.ToolTip = _thumbnailMode
            ? "選んだ画像を開く (T / Enter)"
            : "サムネイル一覧 (T)";
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
        if (_slideshow || _navigator.Count == 0) return;

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

        // 最小化している間は送らない（見ていないので）。戻すまで待ち直す
        if (WindowState == WindowState.Minimized)
        {
            ScheduleSlide();
            return;
        }

        AdvanceSlide();
    }

    private void AnimationPlayer_PlayCompleted(object? sender, EventArgs e)
    {
        if (!_slideshow || !_slideWaitingForAnimation) return;

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
        SlideshowButton.IsEnabled = _navigator.Count > 0;
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
    // 情報パネル（EXIF）
    // ------------------------------------------------------------

    private void ToggleInfoPanel()
    {
        _infoVisible = !_infoVisible;
        InfoView.Visibility = _infoVisible ? Visibility.Visible : Visibility.Collapsed;
        InfoButton.IsChecked = _infoVisible;

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
    /// </summary>
    private void UpdateImageBaseSize()
    {
        if (_bitmap is null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        ImageView.Width = _bitmap.PixelWidth / dpi.DpiScaleX;
        ImageView.Height = _bitmap.PixelHeight / dpi.DpiScaleY;
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
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            SaveSettings();
        }
    }

    private void RestoreWindowPlacement()
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

        // 保存した位置が画面外になっていても（モニタを外した等）、OS が画面内に収めてくれる
        if (User32.SetWindowPlacement(hwnd, ref placement) && bounds.Maximized)
        {
            // Show の前に設定しておけば、上で決めた位置のモニタで最大化して出る
            WindowState = WindowState.Maximized;
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
        IndexText.Text = $"{_navigator.Index + 1} / {_navigator.Count}";

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
            PixelSizeText.Text = $"{_bitmap.PixelWidth} × {_bitmap.PixelHeight}";
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
                Navigate(_navigator.MoveNext(), +1);
                break;

            case Key.Left:
            case Key.PageUp:
            case Key.Back:
                Navigate(_navigator.MovePrevious(), -1);
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

            // 選択中の画像を開いて、そこからスライドショー
            case Key.S:
                if (!e.IsRepeat)
                {
                    StartSlideshow();
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
            if (e.Delta > 0)
            {
                Navigate(_navigator.MovePrevious(), -1);
            }
            else
            {
                Navigate(_navigator.MoveNext(), +1);
            }
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
