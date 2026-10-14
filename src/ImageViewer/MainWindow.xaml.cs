using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using ImageViewer.Navigation;
using Microsoft.Win32;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private const string AppName = "ImageViewer";
    private const double MinZoom = 0.02;   // 2%
    private const double MaxZoom = 32.0;   // 3200%
    private const double ZoomStep = 1.25;

    private const string GlyphFullScreen = "";
    private const string GlyphBackToWindow = "";

    private readonly ImageLoader _loader = ImageLoader.CreateDefault();
    private readonly FolderNavigator _navigator = new();

    private BitmapSource? _bitmap;

    // 表示状態。_zoom は「画像1ピクセル = 画面1ピクセル」を 1.0 とした倍率
    private bool _fitMode = true;
    private double _zoom = 1.0;
    private Vector _offset;   // 回転・拡縮後の画像の外接矩形の左上（Viewport座標）
    private int _rotation;    // 時計回りの角度（0 / 90 / 180 / 270）。表示のみで、ファイルには書き込まない

    // 全画面表示
    private bool _isFullScreen;
    private WindowState _restoreWindowState;

    // 連打で古い読み込み結果が後から表示されないようにするための通し番号
    private int _loadVersion;

    // ドラッグでのパン
    private Point? _dragStart;
    private Vector _dragStartOffset;

    public MainWindow()
    {
        InitializeComponent();
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

        _ = ShowCurrentAsync();
    }

    private void Navigate(bool moved)
    {
        if (moved)
        {
            _ = ShowCurrentAsync();
        }
    }

    private async Task ShowCurrentAsync()
    {
        int version = ++_loadVersion;
        var path = _navigator.Current;
        if (path is null)
        {
            SetBitmap(null);
            UpdateStatus();
            return;
        }

        UpdateStatus();

        try
        {
            var bitmap = await _loader.LoadAsync(path);
            if (version != _loadVersion)
            {
                return; // もっと新しい画像の読み込みが始まっている
            }

            HideMessage();
            SetBitmap(bitmap);
        }
        catch (ImageLoadException ex)
        {
            if (version != _loadVersion)
            {
                return;
            }

            SetBitmap(null);
            ShowMessage(ex.Message);
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

    private void SetBitmap(BitmapSource? bitmap)
    {
        _bitmap = bitmap;
        ImageView.Source = bitmap;
        _rotation = 0; // 回転は画像ごとにリセット

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
            return;
        }

        var fileName = Path.GetFileName(path);

        // タスクバーで見分けられるよう、タイトルにはファイル名だけ残す
        Title = $"{fileName} - {AppName}";

        FileNameText.Text = fileName;
        FileNameText.ToolTip = path;
        IndexText.Text = $"{_navigator.Index + 1} / {_navigator.Count}";

        if (_bitmap is not null)
        {
            PixelSizeText.Text = $"{_bitmap.PixelWidth} × {_bitmap.PixelHeight}";
            ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        }
        else
        {
            PixelSizeText.Text = string.Empty;
            ZoomText.Text = string.Empty;
        }
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

            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                Navigate(_navigator.MoveNext());
                break;

            case Key.Left:
            case Key.PageUp:
            case Key.Back:
                Navigate(_navigator.MovePrevious());
                break;

            case Key.Home:
                Navigate(_navigator.MoveFirst());
                break;

            case Key.End:
                Navigate(_navigator.MoveLast());
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
            Navigate(e.Delta > 0 ? _navigator.MovePrevious() : _navigator.MoveNext());
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
