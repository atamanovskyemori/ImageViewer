using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Animation;
using ImageViewer.Imaging;
using ImageViewer.Interop;
using ImageViewer.Navigation;
using ImageViewer.Settings;
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

    private const long BytesPerMB = 1024L * 1024;

    // 起動時に ImageViewer.ini から読み、終了時に書き戻す
    private readonly AppSettings _settings = AppSettings.Load(AppSettings.DefaultPath);

    // メモリ上限は設定から決まるので、コンストラクタで作る
    private readonly ImageCache _cache;
    private readonly FolderNavigator _navigator = new();
    private readonly AnimationPlayer _player = new();

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

        // 開き直したときは、ファイルが更新されているかもしれないので読み直す
        _cache.Clear();
        _direction = 1;
        _ = ShowCurrentAsync();
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

        RotateLeftButton.IsEnabled = bitmap is not null;
        RotateRightButton.IsEnabled = bitmap is not null;

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
        UpdateStatus();

        if (_navigator.Current is string current)
        {
            UpdatePrefetch(current);
        }
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

        // メモリ上限は手で編集する項目なので、起動中に ini を直されていたらそちらを残す（上書きで消さない）
        var onDisk = AppSettings.Load(AppSettings.DefaultPath);
        _settings.AnimationMaxMemoryMB = onDisk.AnimationMaxMemoryMB;
        _settings.PrefetchMaxMemoryMB = onDisk.PrefetchMaxMemoryMB;

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
        _player.Suspended = WindowState == WindowState.Minimized;
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

        if (_bitmap is null)
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

        switch (e.Key)
        {
            case Key.O when ctrl:
                ShowOpenDialog();
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
