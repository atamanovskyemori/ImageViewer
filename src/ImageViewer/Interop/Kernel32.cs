using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImageViewer.Interop;

/// <summary>kernel32.dll の P/Invoke 定義</summary>
internal static class Kernel32
{
    /// <summary>名前付きパイプの相手（サーバー側）のプロセスID を取る。起動を1つにまとめるときに使う</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
