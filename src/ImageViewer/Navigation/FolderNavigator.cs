using System.IO;

namespace ImageViewer.Navigation;

/// <summary>
/// 裏で作ったフォルダの画像一覧（<see cref="FolderNavigator.ListFolderOf"/> の結果）。
/// 作ったときの並び順も持っておき、入れ替えるときに変わっていたら並べ直す。
/// </summary>
public sealed record FolderListing(string Directory, IReadOnlyList<string> Files, FileSortKey SortKey, bool SortDescending);

/// <summary>
/// 開いた画像と同じフォルダ内の画像一覧を持ち、前後の移動を管理する。
/// 表示やデコードには関与しない（純粋なリスト管理）。
/// </summary>
public sealed class FolderNavigator
{
    /// <summary>
    /// OS にコーデック（Microsoft Store の拡張機能）が入っていれば WIC で読める拡張子。
    /// 入っていないと読めないので、読めなかったときにその旨を出すのに使う。
    /// </summary>
    public static readonly IReadOnlySet<string> CodecRequiredExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif", ".hif",
            ".avif",
            ".jxl",
        };

    /// <summary>フォルダ走査の対象にする拡張子</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(
            [
                ".jpg", ".jpeg", ".jpe", ".jfif",
                ".png",
                ".gif",
                ".bmp", ".dib",
                ".tif", ".tiff",
                ".ico",
                ".webp",
                ".jxr", ".wdp",
                .. CodecRequiredExtensions,
            ],
            StringComparer.OrdinalIgnoreCase);

    private List<string> _files = [];
    private int _index = -1;
    private string? _directory;

    public string? Current => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    /// <summary>0始まりの現在位置（何も開いていなければ -1）</summary>
    public int Index => _index;

    public int Count => _files.Count;

    /// <summary>今開いているフォルダ（フルパス）。何も開いていなければ null</summary>
    public string? CurrentDirectory => _directory;

    /// <summary>今の並び順の画像一覧（サムネイル一覧に使う）。並び順を変えると中身の順番も変わる</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>現在の並び順の種類</summary>
    public FileSortKey SortKey { get; private set; } = FileSortKey.Name;

    /// <summary>降順なら true</summary>
    public bool SortDescending { get; private set; }

    /// <summary>
    /// 並び順を変える。表示中の画像はそのままで、一覧の順番と現在位置だけが変わる。
    /// まだ何も開いていなくても設定でき、次に開いたフォルダから適用される。
    /// </summary>
    public void SetSort(FileSortKey key, bool descending)
    {
        if (key == SortKey && descending == SortDescending)
        {
            return;
        }

        SortKey = key;
        SortDescending = descending;

        var current = Current;
        Sort(_files);
        if (current is not null)
        {
            _index = _files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 現在位置から offset だけ離れた画像のパスを返す（範囲外なら null）。位置は動かさない。
    /// 先読み対象を決めるのに使う。
    /// </summary>
    public string? PeekRelative(int offset)
    {
        if (_index < 0) return null;

        int index = _index + offset;
        return index >= 0 && index < _files.Count ? _files[index] : null;
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// ファイルを開き、同じフォルダの画像を一覧化する。
    /// 対応外の拡張子でも、明示的に開かれたファイルは一覧に入れて表示を試みる。
    /// （一覧を裏で作るときは <see cref="OpenSingleFile"/> → <see cref="ListFolderOf"/> → <see cref="ApplyListing"/>）
    /// </summary>
    public bool OpenFile(string filePath)
    {
        if (!OpenSingleFile(filePath))
        {
            return false;
        }

        ApplyListing(ListFolderOf(Current!, SortKey, SortDescending));
        return true;
    }

    /// <summary>
    /// ファイルを1枚だけの一覧として開く（フォルダの一覧ができるまでのつなぎ）。
    /// フォルダは列挙しないので、ファイル数の多いフォルダやネットワークドライブでもすぐ終わる。
    /// </summary>
    public bool OpenSingleFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            return false;
        }

        _files = [fullPath];
        _index = 0;
        _directory = directory;
        return true;
    }

    /// <summary>
    /// ファイルと同じフォルダの画像を列挙して並べる（ファイル自体も必ず入れる）。
    /// UI に触らないので、裏のスレッドから呼んでいい。
    /// </summary>
    public static FolderListing ListFolderOf(string fullPath, FileSortKey key, bool descending)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("フォルダの中のファイルではありません。", nameof(fullPath));

        var files = EnumerateImages(directory);
        if (!files.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(fullPath);
        }

        Sort(files, key, descending);
        return new FolderListing(directory, files, key, descending);
    }

    /// <summary>
    /// 裏で作った一覧に入れ替える。今のファイルはそのまま（位置だけ一覧の中に合わせる）。
    /// 作っている間に並び順が変えられていたら、今の並び順で並べ直す。
    /// </summary>
    /// <returns>今のファイルと同じフォルダの一覧で、入れ替えたら true</returns>
    public bool ApplyListing(FolderListing listing)
    {
        var current = Current;
        if (current is null || !string.Equals(listing.Directory, _directory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var files = new List<string>(listing.Files);
        if (!files.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(current);
        }
        if (listing.SortKey != SortKey || listing.SortDescending != SortDescending
            || files.Count != listing.Files.Count)
        {
            Sort(files);
        }

        _files = files;
        _index = files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>フォルダを開き、先頭の画像を選択する（selectLast なら最後の画像）</summary>
    public bool OpenDirectory(string directoryPath, bool selectLast = false)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        var files = EnumerateImages(fullPath);
        if (files.Count == 0)
        {
            return false;
        }

        Sort(files);

        _files = files;
        _index = selectLast ? files.Count - 1 : 0;
        _directory = Path.TrimEndingDirectorySeparator(fullPath);
        return true;
    }

    /// <summary>フォルダに表示できる画像が1枚でもあるか。読めないフォルダは「無い」扱い</summary>
    public static bool ContainsImages(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath).Any(IsSupported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool MoveNext() => MoveTo(_index + 1);

    public bool MovePrevious() => MoveTo(_index - 1);

    public bool MoveFirst() => MoveTo(0);

    public bool MoveLast() => MoveTo(_files.Count - 1);

    /// <summary>指定した位置へ移動する（サムネイル一覧で選んだとき）。位置が変わったら true</summary>
    public bool MoveToIndex(int index) => MoveTo(index);

    /// <summary>端では止まる（ループしない）。位置が変わったら true。</summary>
    private bool MoveTo(int index)
    {
        if (index < 0 || index >= _files.Count || index == _index)
        {
            return false;
        }

        _index = index;
        return true;
    }

    /// <summary>
    /// 今の並び順で並べる。昇順・降順を切り替えただけのときも、逆順にするのではなく並べ直す
    /// （主キーが同じものはいつも名前の昇順にしたいのと、更新日時・サイズを読み直したいので）。
    /// </summary>
    private void Sort(List<string> files) => Sort(files, SortKey, SortDescending);

    private static void Sort(List<string> files, FileSortKey key, bool descending) =>
        files.Sort(new FileSortComparer(files, key, descending));

    /// <summary>フォルダ内の対応画像を列挙する（並べ替えはしない）</summary>
    private static List<string> EnumerateImages(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(IsSupported)
            .ToList();
}
