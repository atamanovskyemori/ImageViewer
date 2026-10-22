using System.Globalization;
using System.IO;
using System.Text;
using ImageViewer.Navigation;
using ImageViewer.Thumbnails;

namespace ImageViewer.Settings;

/// <summary>
/// 通常時のウィンドウの位置とサイズ（物理ピクセル。GetWindowPlacement の rcNormalPosition と同じ座標）。
/// 最大化・全画面のときも、元に戻したときの位置とサイズを持つ。
/// </summary>
public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized);

/// <summary>画像の後ろ（透明な部分）に敷くもの。B で順に切り替える</summary>
public enum ImageBackground
{
    /// <summary>何も敷かない（ウィンドウと同じ暗い灰色）</summary>
    Default,

    /// <summary>市松模様（どこが透明か分かる）</summary>
    Checker,

    /// <summary>白</summary>
    White,
}

/// <summary>
/// アプリの設定。exe と同じフォルダの ImageViewer.ini に保存する。
/// 読めない値・壊れた値は無視して既定値を使う（設定が原因で起動できない、ということがないように）。
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "ImageViewer.ini";

    // ウィンドウとして意味のある最小サイズ（これより小さい値は壊れているとみなす）
    private const int MinWindowSize = 100;

    private const string WindowSection = "Window";
    private const string SortSection = "Sort";
    private const string MemorySection = "Memory";
    private const string ThumbnailSection = "Thumbnail";
    private const string SlideshowSection = "Slideshow";
    private const string InfoPanelSection = "InfoPanel";
    private const string StartupSection = "Startup";
    private const string ViewSection = "View";

    /// <summary>アニメーション1枚のメモリ上限の既定値（MB）</summary>
    public const int DefaultAnimationMaxMemoryMB = 256;

    /// <summary>先読みに使うメモリ上限の既定値（MB）</summary>
    public const int DefaultPrefetchMaxMemoryMB = 512;

    /// <summary>サムネイルに使うメモリ上限の既定値（MB）</summary>
    public const int DefaultThumbnailMaxMemoryMB = 256;

    /// <summary>スライドショーで1枚を表示する時間の既定値（秒）</summary>
    public const double DefaultSlideshowIntervalSeconds = 5;

    // スライドショーの時間として受け付ける範囲（秒）。範囲外は既定値に戻す
    public const double MinSlideshowIntervalSeconds = 0.5;
    public const double MaxSlideshowIntervalSeconds = 3600;

    /// <summary>情報パネルの幅の既定値と範囲（DIP）</summary>
    public const int DefaultInfoPanelWidth = 300;
    public const int MinInfoPanelWidth = 200;
    public const int MaxInfoPanelWidth = 1200;

    // メモリ上限として受け付ける範囲（MB）。範囲外は既定値に戻す
    private const int MinMemoryMB = 1;
    private const int MaxMemoryMB = 65536;

    /// <summary>
    /// アニメーション1枚に使っていいメモリ（MB）。超えたら1コマ目だけ表示する。
    /// アプリには変更する画面がないので、ini を手で編集して変える。
    /// </summary>
    public int AnimationMaxMemoryMB { get; set; } = DefaultAnimationMaxMemoryMB;

    /// <summary>
    /// 先読みした画像の合計メモリ（MB）。表示中の画像は含まない。ini を手で編集して変える。
    /// </summary>
    public int PrefetchMaxMemoryMB { get; set; } = DefaultPrefetchMaxMemoryMB;

    /// <summary>
    /// サムネイルの合計メモリ（MB）。超えたら長く使っていないものから捨てる。ini を手で編集して変える。
    /// </summary>
    public int ThumbnailMaxMemoryMB { get; set; } = DefaultThumbnailMaxMemoryMB;

    /// <summary>サムネイル一覧の大きさ（DIP）。Ctrl+ホイールで変えたものを保存する</summary>
    public int ThumbnailSize { get; set; } = ThumbnailLayout.DefaultSize;

    /// <summary>
    /// スライドショーで静止画1枚を表示する時間（秒、小数も可）。アニメーションは時間に関係なく1周したら次へ。
    /// ini を手で編集して変える。
    /// </summary>
    public double SlideshowIntervalSeconds { get; set; } = DefaultSlideshowIntervalSeconds;

    /// <summary>情報パネルの幅（DIP）。境目をドラッグして変えたものを保存する</summary>
    public int InfoPanelWidth { get; set; } = DefaultInfoPanelWidth;

    /// <summary>
    /// 起動を1つにまとめるか。true なら、2回目以降の起動は起動中のウィンドウで開く。
    /// 2つのウィンドウで見比べたいときは false にする。ini を手で編集して変える（次の起動から効く）。
    /// </summary>
    public bool SingleInstance { get; set; } = true;

    /// <summary>画像の後ろ（透明な部分）に敷くもの。B で切り替えたものを保存する</summary>
    public ImageBackground ImageBackground { get; set; } = ImageBackground.Default;

    /// <summary>情報パネルの幅を、受け付ける範囲に収める</summary>
    public static int ClampInfoPanelWidth(int width) => Math.Clamp(width, MinInfoPanelWidth, MaxInfoPanelWidth);

    /// <summary>保存されたウィンドウの位置。まだ保存されていなければ null（既定の位置で開く）</summary>
    public WindowBounds? Window { get; set; }

    public FileSortKey SortKey { get; set; } = FileSortKey.Name;

    public bool SortDescending { get; set; }

    /// <summary>exe と同じフォルダの ImageViewer.ini</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>設定ファイルを読む。無い・読めないときは既定値を返す。</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return FromIni(IniFile.Parse(File.ReadAllText(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// 設定ファイルに書き込む。書けなかったら false（exe を書き込み禁止の場所に置いたときなど）。
    /// 書き込み途中で落ちても元のファイルが壊れないよう、一時ファイルに書いてから置き換える。
    /// </summary>
    public bool TrySave(string path)
    {
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, ToIni().ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return false;
        }
    }

    public static AppSettings FromIni(IniFile ini)
    {
        var settings = new AppSettings();

        if (TryGetInt(ini, WindowSection, "Left", out int left)
            && TryGetInt(ini, WindowSection, "Top", out int top)
            && TryGetInt(ini, WindowSection, "Width", out int width)
            && TryGetInt(ini, WindowSection, "Height", out int height)
            && width >= MinWindowSize
            && height >= MinWindowSize)
        {
            bool maximized = TryGetBool(ini, WindowSection, "Maximized", out bool m) && m;
            settings.Window = new WindowBounds(left, top, width, height, maximized);
        }

        if (Enum.TryParse(ini.Get(SortSection, "Key"), ignoreCase: true, out FileSortKey key)
            && Enum.IsDefined(key))
        {
            settings.SortKey = key;
        }

        if (TryGetBool(ini, SortSection, "Descending", out bool descending))
        {
            settings.SortDescending = descending;
        }

        if (TryGetMemoryMB(ini, "AnimationMaxMB", out int animationMB))
        {
            settings.AnimationMaxMemoryMB = animationMB;
        }

        if (TryGetMemoryMB(ini, "PrefetchMaxMB", out int prefetchMB))
        {
            settings.PrefetchMaxMemoryMB = prefetchMB;
        }

        if (TryGetMemoryMB(ini, "ThumbnailMaxMB", out int thumbnailMB))
        {
            settings.ThumbnailMaxMemoryMB = thumbnailMB;
        }

        if (double.TryParse(ini.Get(SlideshowSection, "IntervalSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double interval)
            && interval is >= MinSlideshowIntervalSeconds and <= MaxSlideshowIntervalSeconds)
        {
            settings.SlideshowIntervalSeconds = interval;
        }

        // 範囲外の幅（手で書いた 50 など）は、範囲の端に収める
        if (TryGetInt(ini, InfoPanelSection, "Width", out int infoWidth))
        {
            settings.InfoPanelWidth = ClampInfoPanelWidth(infoWidth);
        }

        if (Enum.TryParse(ini.Get(ViewSection, "Background"), ignoreCase: true, out ImageBackground background)
            && Enum.IsDefined(background))
        {
            settings.ImageBackground = background;
        }

        if (TryGetBool(ini, StartupSection, "SingleInstance", out bool singleInstance))
        {
            settings.SingleInstance = singleInstance;
        }

        // 選べる大きさ以外の値（手で書いた 150 など）は、一番近い大きさにする
        if (TryGetInt(ini, ThumbnailSection, "Size", out int thumbnailSize))
        {
            settings.ThumbnailSize = ThumbnailLayout.Normalize(thumbnailSize);
        }

        return settings;
    }

    public IniFile ToIni()
    {
        var ini = new IniFile();

        if (Window is { } w)
        {
            ini.Set(WindowSection, "Left", Format(w.Left));
            ini.Set(WindowSection, "Top", Format(w.Top));
            ini.Set(WindowSection, "Width", Format(w.Width));
            ini.Set(WindowSection, "Height", Format(w.Height));
            ini.Set(WindowSection, "Maximized", Format(w.Maximized));
        }

        ini.Set(SortSection, "Key", SortKey.ToString());
        ini.Set(SortSection, "Descending", Format(SortDescending));

        // 手で編集できるように、既定値のままでも必ず書き出す
        ini.Set(MemorySection, "AnimationMaxMB", Format(AnimationMaxMemoryMB));
        ini.Set(MemorySection, "PrefetchMaxMB", Format(PrefetchMaxMemoryMB));
        ini.Set(MemorySection, "ThumbnailMaxMB", Format(ThumbnailMaxMemoryMB));

        ini.Set(ThumbnailSection, "Size", Format(ThumbnailLayout.Normalize(ThumbnailSize)));

        ini.Set(SlideshowSection, "IntervalSeconds", SlideshowIntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        ini.Set(InfoPanelSection, "Width", Format(ClampInfoPanelWidth(InfoPanelWidth)));

        ini.Set(ViewSection, "Background", ImageBackground.ToString());

        // 手で編集できるように、既定値のままでも必ず書き出す
        ini.Set(StartupSection, "SingleInstance", Format(SingleInstance));

        return ini;
    }

    private static bool TryGetMemoryMB(IniFile ini, string key, out int value) =>
        TryGetInt(ini, MemorySection, key, out value) && value is >= MinMemoryMB and <= MaxMemoryMB;

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";

    private static bool TryGetInt(IniFile ini, string section, string key, out int value) =>
        int.TryParse(ini.Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>true / false のほか、手で書いたときのために 1 / 0 も受け付ける</summary>
    private static bool TryGetBool(IniFile ini, string section, string key, out bool value)
    {
        switch (ini.Get(section, key)?.ToLowerInvariant())
        {
            case "true" or "1":
                value = true;
                return true;
            case "false" or "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても実害はない
        }
    }
}
