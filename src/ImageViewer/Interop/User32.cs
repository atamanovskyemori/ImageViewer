using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// ウィンドウ位置の保存・復元、モニタの大きさの取得、2回目の起動でのウィンドウの呼び出しに使う user32.dll の P/Invoke 定義。
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

    // ------------------------------------------------------------
    // モニタの大きさ（画面に合わせて縮めて読む大きさを決めるのに使う）
    // ------------------------------------------------------------

    /// <summary>ウィンドウがどのモニタにも重なっていなければ、一番近いモニタを返す</summary>
    public const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public int Flags;

        /// <summary>Size を設定済みのものを作る（Size が無いと API が失敗する）</summary>
        public static MONITORINFO Create() => new() { Size = Marshal.SizeOf<MONITORINFO>() };
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ------------------------------------------------------------
    // 2回目の起動から、起動中のウィンドウを前に出す
    // ------------------------------------------------------------

    /// <summary>最小化・最大化を元に戻す（最大化中に最小化していたら、最大化に戻る）</summary>
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>指定したプロセスが、ウィンドウを前に出せるようにする（呼べるのは、今前に出ている側だけ）</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    /// <summary>ウィンドウが操作できるか（ダイアログを出している間は false）</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(IntPtr hWnd);
}
