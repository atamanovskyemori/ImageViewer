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
/// 日時・サイズは比較のたびにディスクを見に行かないよう、作成時にまとめて取得しておく（フォルダごとに1回の列挙で）。
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
        _info = key != FileSortKey.Name ? ReadInfo(paths) : new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 日時・サイズをまとめて取る。ファイルごとに問い合わせると、ネットワークドライブでは1つずつ往復して遅いので、
    /// フォルダごとに1回だけ列挙して、列挙で一緒に返ってくる日時・サイズをそのまま使う。
    /// 列挙で見つからなかったもの（列挙した後に消された・フォルダが読めない）だけ、1つずつ取りに行く。
    /// </summary>
    private static Dictionary<string, (DateTime LastWriteTime, long Size)> ReadInfo(IEnumerable<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var info = new Dictionary<string, (DateTime LastWriteTime, long Size)>(StringComparer.OrdinalIgnoreCase);

        var directories = wanted
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
                {
                    if (wanted.Contains(file.FullName))
                    {
                        info[file.FullName] = (file.LastWriteTimeUtc, file.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 下で1つずつ取りに行く
            }
        }

        foreach (var path in wanted)
        {
            if (!info.ContainsKey(path))
            {
                info[path] = GetInfo(path);
            }
        }
        return info;
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
