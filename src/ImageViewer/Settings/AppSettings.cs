using System.Globalization;
using System.IO;
using System.Text;
using ImageViewer.Navigation;

namespace ImageViewer.Settings;

/// <summary>
/// 通常時のウィンドウの位置とサイズ（物理ピクセル。GetWindowPlacement の rcNormalPosition と同じ座標）。
/// 最大化・全画面のときも、元に戻したときの位置とサイズを持つ。
/// </summary>
public sealed record WindowBounds(int Left, int Top, int Width, int Height, bool Maximized);

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

        return ini;
    }

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
