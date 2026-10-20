using System.IO;

namespace ImageViewer.Navigation;

/// <summary>
/// 「次のフォルダ / 前のフォルダ」を探す。範囲（scopeRoot）の中のフォルダを、次の順番でたどる。
/// <code>
/// scopeRoot\
///   A\          1
///     A1\       2   （フォルダの次は、その中のサブフォルダ）
///       A1x\    3   （さらに奥まで全部）
///     A2\       4
///   B\          5   （最後のサブフォルダの次は、元の階層の次のフォルダ）
/// </code>
/// 「前」はこの順番を逆にたどる（B の前は A2、A1 の前は A）。
/// <list type="bullet">
/// <item>同じ階層では、フォルダ名の自然順（ファイルの並び順の設定とは関係なく、いつも名前の昇順）</item>
/// <item>表示できる画像が無いフォルダは飛ばして、さらに先を探す</item>
/// <item>隠しフォルダ・システムフォルダ（$RECYCLE.BIN など）、ジャンクション・シンボリックリンクには入らない（ループ対策）</item>
/// <item>scopeRoot より上には行かない。端まで行ったら null（反対の端には回らない）</item>
/// </list>
/// ディスクを見に行くので、UIスレッドではなく裏で呼ぶ。
/// </summary>
public static class FolderSiblings
{
    // 深すぎる・多すぎるフォルダ構成で延々と探し続けないための上限
    private const int MaxDepth = 64;
    private const int MaxSteps = 100_000;

    /// <param name="folder">今のフォルダ</param>
    /// <param name="scopeRoot">たどる範囲。この中（このフォルダより下）だけを探す</param>
    /// <param name="direction">+1: 次のフォルダ / -1: 前のフォルダ</param>
    public static string? Find(string folder, string scopeRoot, int direction) =>
        Find(folder, scopeRoot, direction, FolderNavigator.ContainsImages);

    /// <param name="hasImages">フォルダに画像があるかの判定（テストで差し替えられるように）</param>
    public static string? Find(string folder, string scopeRoot, int direction, Func<string, bool> hasImages)
    {
        if (direction == 0)
        {
            return null;
        }

        string current, root;
        try
        {
            current = Normalize(folder);
            root = Normalize(scopeRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsSameOrInside(current, root) || !Directory.Exists(current))
        {
            return null; // 範囲の外・消されたフォルダ
        }

        string? candidate = current;
        for (int step = 0; step < MaxSteps; step++)
        {
            candidate = direction > 0 ? Next(candidate, root) : Previous(candidate, root);
            if (candidate is null)
            {
                return null;
            }
            if (hasImages(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 範囲の一番上（scopeRoot）からの相対パス（「2024\旅行」など）。ダイアログやお知らせに出す名前
    /// </summary>
    public static string DisplayName(string folder, string scopeRoot)
    {
        try
        {
            var relative = Path.GetRelativePath(scopeRoot, folder);
            return relative is "." or "" ? Path.GetFileName(Normalize(folder)) : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Path.GetFileName(folder);
        }
    }

    // ------------------------------------------------------------
    // たどり方
    // ------------------------------------------------------------

    /// <summary>次のフォルダ：サブフォルダがあればその最初。無ければ、自分か親の「次の兄弟」</summary>
    private static string? Next(string folder, string root)
    {
        if (Depth(folder, root) < MaxDepth && Children(folder) is [var firstChild, ..])
        {
            return firstChild;
        }

        // サブフォルダが無い（またはもう全部たどった）。上へ戻りながら、次の兄弟を探す
        string node = folder;
        while (!IsSame(node, root))
        {
            var parent = Path.GetDirectoryName(node);
            if (parent is null)
            {
                return null;
            }

            var siblings = SiblingsIncluding(parent, node);
            int index = IndexOf(siblings, node);
            if (index + 1 < siblings.Count)
            {
                return siblings[index + 1];
            }

            node = parent;
        }

        return null; // 範囲の最後まで来た
    }

    /// <summary>前のフォルダ：前の兄弟があれば、その一番奥の最後のサブフォルダ。無ければ親</summary>
    private static string? Previous(string folder, string root)
    {
        if (IsSame(folder, root))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(folder);
        if (parent is null)
        {
            return null;
        }

        var siblings = SiblingsIncluding(parent, folder);
        int index = IndexOf(siblings, folder);
        if (index > 0)
        {
            return LastDescendant(siblings[index - 1], root);
        }

        // 一番前の兄弟だった。前は親（範囲の一番上そのものは対象にしない）
        return IsSame(parent, root) ? null : parent;
    }

    /// <summary>フォルダの中を、最後のサブフォルダへ最後のサブフォルダへと一番奥までたどる</summary>
    private static string LastDescendant(string folder, string root)
    {
        string node = folder;
        while (Depth(node, root) < MaxDepth && Children(node) is { Count: > 0 } children)
        {
            node = children[^1];
        }
        return node;
    }

    /// <summary>入っていいサブフォルダを、名前の自然順で。読めなければ空</summary>
    private static List<string> Children(string folder)
    {
        try
        {
            var children = new DirectoryInfo(folder)
                .EnumerateDirectories()
                .Where(CanEnter)
                .Select(d => d.FullName)
                .ToList();
            children.Sort(NaturalFileNameComparer.Instance);
            return children;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// parent の中の兄弟たち。self が隠しフォルダなどで普段は入らないものでも（そこを開いていることはある）、位置を決めるために入れる
    /// </summary>
    private static List<string> SiblingsIncluding(string parent, string self)
    {
        var siblings = Children(parent);
        if (IndexOf(siblings, self) < 0)
        {
            siblings.Add(self);
            siblings.Sort(NaturalFileNameComparer.Instance);
        }
        return siblings;
    }

    private static bool CanEnter(DirectoryInfo directory)
    {
        try
        {
            const FileAttributes skip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
            return (directory.Attributes & skip) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int IndexOf(List<string> folders, string folder) =>
        folders.FindIndex(f => IsSame(f, folder));

    private static int Depth(string folder, string root)
    {
        var relative = Path.GetRelativePath(root, folder);
        return relative == "." ? 0 : relative.Split(Path.DirectorySeparatorChar).Length;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSame(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string folder, string root) =>
        IsSame(folder, root)
        || folder.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
