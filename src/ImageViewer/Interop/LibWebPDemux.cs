using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// libwebpdemux.dll の P/Invoke 定義（アニメーションWebP の WebPAnimDecoder、demux.h）。
/// libwebpdemux.dll は libwebp.dll に依存するので、両方を exe と同じフォルダに置くこと。
/// <para>
/// C のヘッダでは WebPAnimDecoderNew / WebPAnimDecoderOptionsInit はマクロで、
/// 実体は ABI バージョンを渡す ...Internal 関数なので、そちらを呼ぶ。
/// </para>
/// </summary>
internal static unsafe class LibWebPDemux
{
    private const string DllName = "libwebpdemux";

    /// <summary>
    /// WEBP_DEMUX_ABI_VERSION。上位8ビット（メジャー）が DLL と合っていれば使える。
    /// libwebp 1.x は 0x0107。
    /// </summary>
    public const int AbiVersion = 0x0107;

    /// <summary>WEBP_CSP_MODE の MODE_BGRA（非乗算済みアルファ）</summary>
    public const int ModeBgra = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct WebPData
    {
        public byte* Bytes;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WebPAnimDecoderOptions
    {
        public int ColorMode;     // WEBP_CSP_MODE
        public int UseThreads;
        public fixed uint Padding[7];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WebPAnimInfo
    {
        public uint CanvasWidth;
        public uint CanvasHeight;
        public uint LoopCount;    // 0 = 無限
        public uint BgColor;
        public uint FrameCount;
        public fixed uint Pad[4];
    }

    /// <summary>オプションを既定値で初期化する。ABI が合わなければ 0 を返す。</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern int WebPAnimDecoderOptionsInitInternal(WebPAnimDecoderOptions* options, int abiVersion);

    /// <summary>
    /// デコーダを作る。失敗したら NULL。
    /// data が指すメモリは、デコーダを Delete するまで動かさないこと（中でコピーしない）。
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern IntPtr WebPAnimDecoderNewInternal(WebPData* data, WebPAnimDecoderOptions* options, int abiVersion);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern int WebPAnimDecoderGetInfo(IntPtr decoder, WebPAnimInfo* info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern int WebPAnimDecoderHasMoreFrames(IntPtr decoder);

    /// <summary>
    /// 次のコマを合成済みキャンバス（canvas_width × canvas_height × 4 バイト）で取り出す。
    /// buffer はデコーダが持っているメモリで、次の呼び出しまで有効。timestamp はそのコマの「終わる時刻」（ms、累積）。
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern int WebPAnimDecoderGetNext(IntPtr decoder, byte** buffer, int* timestamp);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern void WebPAnimDecoderDelete(IntPtr decoder);

    private static readonly Lazy<bool> s_isAvailable = new(() =>
        NativeLibrary.TryLoad(
            DllName,
            typeof(LibWebPDemux).Assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories,
            out _));

    /// <summary>libwebpdemux.dll がロードできるか（初回のみ判定してキャッシュ）</summary>
    public static bool IsAvailable => s_isAvailable.Value;
}
