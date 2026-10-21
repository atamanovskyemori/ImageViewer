using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// ウィンドウ位置の保存・復元に使う user32.dll の P/Invoke 定義。
/// GetWindowPlacement / SetWindowPlacement を使うと、
/// ・最大化中でも「元に戻したときの位置とサイズ」が取れる
/// ・保存した位置が画面外（モニタを外した等）でも、OS が画面内に収めてくれる
/// </summary>
internal static class User32
{
    public const int SW_HIDE = 0;

    /// <summary>最小化を解除したら最大化に戻る（最大化中に最小化したとき立つ）</summary>
    public const int WPF_RESTORETOMAXIMIZED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;

        /// <summary>Length を設定済みのものを作る（Length が無いと API が失敗する）</summary>
        public static WINDOWPLACEMENT Create() => new() { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}
