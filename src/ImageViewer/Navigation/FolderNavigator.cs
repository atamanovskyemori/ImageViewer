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
            files.Sort(NaturalFileNameComparer.Instance);
        }

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

        _files = files;
        _index = 0;
        return true;
    }

    public bool MoveNext() => MoveTo(_index + 1);

    public bool MovePrevious() => MoveTo(_index - 1);

    public bool MoveFirst() => MoveTo(0);

    public bool MoveLast() => MoveTo(_files.Count - 1);

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

    private static List<string> EnumerateImages(string directory)
    {
        var files = Directory.EnumerateFiles(directory)
            .Where(IsSupported)
            .ToList();
        files.Sort(NaturalFileNameComparer.Instance);
        return files;
    }
}
