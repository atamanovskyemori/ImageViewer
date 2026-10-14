using System.IO;

namespace ImageViewer.Navigation;

/// <summary>フォルダ内の画像の並び順の種類</summary>
public enum FileSortKey
{
    /// <summary>ファイル名（自然順。img2 が img10 より前）</summary>
    Name,

    /// <summary>更新日時</summary>
    LastWriteTime,

    /// <summary>ファイルサイズ</summary>
    Size,
}

/// <summary>
/// 並び順に従ってファイルパスを比較する。
/// 日時・サイズは比較のたびにディスクを見に行かないよう、作成時にまとめて取得しておく。
/// 主キーが同じときは、名前（自然順・昇順）で並べる。
/// </summary>
public sealed class FileSortComparer : IComparer<string>
{
    private readonly FileSortKey _key;
    private readonly bool _descending;
    private readonly Dictionary<string, (DateTime LastWriteTime, long Size)> _info;

    public FileSortComparer(IEnumerable<string> paths, FileSortKey key, bool descending)
    {
        _key = key;
        _descending = descending;
        _info = new(StringComparer.OrdinalIgnoreCase);
        if (key != FileSortKey.Name)
        {
            foreach (var path in paths)
            {
                _info[path] = GetInfo(path);
            }
        }
    }

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return NaturalFileNameComparer.Instance.Compare(x, y);
        }

        int result = _key switch
        {
            FileSortKey.LastWriteTime => _info[x].LastWriteTime.CompareTo(_info[y].LastWriteTime),
            FileSortKey.Size => _info[x].Size.CompareTo(_info[y].Size),
            _ => NaturalFileNameComparer.Instance.Compare(x, y),
        };

        if (_descending)
        {
            result = -result;
        }

        return result != 0 ? result : NaturalFileNameComparer.Instance.Compare(x, y);
    }

    private static (DateTime LastWriteTime, long Size) GetInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);

            // 列挙した後に消されたファイルは、Length が例外を投げるので最小値として扱う
            return info.Exists
                ? (info.LastWriteTimeUtc, info.Length)
                : (DateTime.MinValue, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (DateTime.MinValue, -1);
        }
    }
}
