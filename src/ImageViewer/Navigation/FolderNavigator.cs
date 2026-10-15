using System.IO;

namespace ImageViewer.Navigation;

/// <summary>
/// 開いた画像と同じフォルダ内の画像一覧を持ち、前後の移動を管理する。
/// 表示やデコードには関与しない（純粋なリスト管理）。
/// </summary>
public sealed class FolderNavigator
{
    /// <summary>フォルダ走査の対象にする拡張子</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif",
            ".png",
            ".gif",
            ".bmp", ".dib",
            ".tif", ".tiff",
            ".ico",
            ".webp",
            ".jxr", ".wdp",
        };

    private List<string> _files = [];
    private int _index = -1;

    public string? Current => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    /// <summary>0始まりの現在位置（何も開いていなければ -1）</summary>
    public int Index => _index;

    public int Count => _files.Count;

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
    /// </summary>
    public bool OpenFile(string filePath)
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

        var files = EnumerateImages(directory);
        if (!files.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(fullPath);
        }

        Sort(files);

        _files = files;
        _index = files.FindIndex(f => string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>フォルダを開き、先頭の画像を選択する</summary>
    public bool OpenDirectory(string directoryPath)
    {
        var files = EnumerateImages(Path.GetFullPath(directoryPath));
        if (files.Count == 0)
        {
            return false;
        }

        Sort(files);

        _files = files;
        _index = 0;
        return true;
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

    // キャッシュして「昇順/降順切り替え」のみの場合は再ソートを避ける
    private object _lastSortKey;
    private bool _lastSortDescending;

    private void Sort(List<string> files)
    {
        // 同じソートキーで昇順/降順だけ切り替わった場合は全再ソートを避けて反転する
        if (_lastSortKey != null && Equals(_lastSortKey, SortKey) && _lastSortDescending != SortDescending)
        {
            files.Reverse();
            _lastSortDescending = SortDescending;
            return;
        }

        files.Sort(new FileSortComparer(files, SortKey, SortDescending));

        _lastSortKey = SortKey;
        _lastSortDescending = SortDescending;
    }

    /// <summary>フォルダ内の対応画像を列挙する（並べ替えはしない）</summary>
    private static List<string> EnumerateImages(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(IsSupported)
            .ToList();
}
