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

    // 消された（移動された）けれど、表示中なので一覧に残している画像。離れたら外す
    private string? _removedCurrent;

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
        _removedCurrent = null;
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

        return ListFolder(directory, key, descending, alsoInclude: fullPath);
    }

    /// <summary>
    /// フォルダの画像を列挙して並べる。UI に触らないので、裏のスレッドから呼んでいい。
    /// </summary>
    /// <param name="alsoInclude">対応外の拡張子でも一覧に入れるファイル（明示的に開かれたファイル）</param>
    public static FolderListing ListFolder(string directory, FileSortKey key, bool descending, string? alsoInclude = null)
    {
        var files = EnumerateImages(directory);
        if (alsoInclude is not null && !files.Contains(alsoInclude, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(alsoInclude);
        }

        Sort(files, key, descending);
        return new FolderListing(directory, files, key, descending);
    }

    /// <summary>
    /// 裏で作った一覧に入れ替える。今のファイルはそのまま（位置だけ一覧の中に合わせる）。
    /// 作っている間に並び順が変えられていたら、今の並び順で並べ直す。
    /// </summary>
    /// <param name="keepMissingCurrent">
    /// 今のファイルが一覧に無い（消された・移動された）とき、true なら元の位置あたりに残して、
    /// 別の画像へ移ったときに一覧から外す（1枚表示で、見ている画像がいきなり消えないように）。
    /// false なら一覧から外して、同じ位置の画像を選ぶ（サムネイル一覧）
    /// </param>
    /// <returns>今のファイルと同じフォルダの一覧で、入れ替えたら true</returns>
    public bool ApplyListing(FolderListing listing, bool keepMissingCurrent = true)
    {
        var current = Current;
        if (current is null || !string.Equals(listing.Directory, _directory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var files = new List<string>(listing.Files);
        if (listing.SortKey != SortKey || listing.SortDescending != SortDescending)
        {
            Sort(files);
        }

        _removedCurrent = null;
        if (!files.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            if (keepMissingCurrent || files.Count == 0)
            {
                // 消えたファイルは並べる元の情報が無いので、だいたい元の位置に入れておく
                files.Insert(Math.Min(_index, files.Count), current);
                _removedCurrent = current;
            }
            else
            {
                _files = files;
                _index = Math.Min(_index, files.Count - 1);
                return true;
            }
        }

        _files = files;
        _index = files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// 消された（移動された）けれど、表示中なので一覧に残している画像があれば外す。
    /// 位置は次の画像に移る（最後の画像だったら前の画像）。1枚しかなければ外さない。
    /// </summary>
    /// <returns>外したら true</returns>
    public bool DropRemovedCurrent()
    {
        if (_removedCurrent is null || _files.Count <= 1)
        {
            return false;
        }

        int index = _files.FindIndex(f => string.Equals(f, _removedCurrent, StringComparison.OrdinalIgnoreCase));
        _removedCurrent = null;
        if (index < 0)
        {
            return false;
        }

        _files.RemoveAt(index);
        if (index < _index || _index >= _files.Count)
        {
            _index--;
        }
        return true;
    }

    /// <summary>フォルダを開き、先頭の画像を選択する（selectLast なら最後の画像）</summary>
    public bool OpenDirectory(string directoryPath, bool selectLast = false)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        return OpenListing(ListFolder(fullPath, SortKey, SortDescending), selectLast);
    }

    /// <summary>
    /// 裏で作ったフォルダの一覧（<see cref="ListFolder"/>）で開き、先頭の画像を選択する（selectLast なら最後の画像）。
    /// 作っている間に並び順が変えられていたら、今の並び順で並べ直す。画像が1枚も無ければ何もせず false。
    /// </summary>
    public bool OpenListing(FolderListing listing, bool selectLast = false)
    {
        if (listing.Files.Count == 0)
        {
            return false;
        }

        var files = new List<string>(listing.Files);
        if (listing.SortKey != SortKey || listing.SortDescending != SortDescending)
        {
            Sort(files);
        }

        _files = files;
        _index = selectLast ? files.Count - 1 : 0;
        _directory = Path.TrimEndingDirectorySeparator(listing.Directory);
        _removedCurrent = null;
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

        // 消されたけれど表示中だったので残していた画像は、離れたら一覧から外す
        if (_removedCurrent is not null && !string.Equals(Current, _removedCurrent, StringComparison.OrdinalIgnoreCase))
        {
            int removed = _files.FindIndex(f => string.Equals(f, _removedCurrent, StringComparison.OrdinalIgnoreCase));
            _removedCurrent = null;
            if (removed >= 0)
            {
                _files.RemoveAt(removed);
                if (removed < _index)
                {
                    _index--;
                }
            }
        }
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
