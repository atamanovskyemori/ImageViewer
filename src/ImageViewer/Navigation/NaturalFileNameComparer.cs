using System.IO;
using System.Runtime.InteropServices;

namespace ImageViewer.Navigation;

/// <summary>
/// エクスプローラーと同じ「自然順」でファイル名を比較する（img2 が img10 より前に来る）。
/// Windows の StrCmpLogicalW をそのまま使う。
/// </summary>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return StrCmpLogicalW(Path.GetFileName(x), Path.GetFileName(y));
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
