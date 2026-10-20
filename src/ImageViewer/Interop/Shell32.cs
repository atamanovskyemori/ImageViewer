using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// エクスプローラーでフォルダを開いて、ファイルを選んだ状態にする（SHOpenFolderAndSelectItems）。
/// 「explorer.exe /select,パス」でもできるが、こちらはパスにカンマなどが入っていても確実で、
/// 同じフォルダのウィンドウが開いていればそれを使ってくれる。
/// </summary>
internal static class Shell32
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>
    /// path（ファイル）を選んだ状態で、そのフォルダをエクスプローラーで開く。
    /// path がフォルダなら、そのフォルダを開く。失敗したら false。
    /// UIスレッド（STA）から呼ぶこと。
    /// </summary>
    public static bool OpenFolderAndSelect(string path)
    {
        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // ファイルの pidl を1つだけ渡す（cidl = 0）と、親フォルダを開いてそのファイルを選ぶ
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }
}
