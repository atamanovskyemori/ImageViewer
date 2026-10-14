using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// libwebp.dll の必要最小限の P/Invoke 定義（decode.h）。
/// メモリは C# 側で確保した配列に直接書き込ませる（WebPDecodeBGRAInto）ので、
/// ネイティブ側で確保したメモリの解放（WebPFree）は不要。
/// </summary>
internal static class LibWebP
{
    private const string DllName = "libwebp";

    /// <summary>
    /// ヘッダから幅と高さを取得する。成功すると 0 以外を返す。
    /// int WebPGetInfo(const uint8_t* data, size_t data_size, int* width, int* height);
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern unsafe int WebPGetInfo(byte* data, nuint dataSize, int* width, int* height);

    /// <summary>
    /// BGRA（非乗算済みアルファ）で、指定バッファにデコードする。失敗すると NULL を返す。
    /// uint8_t* WebPDecodeBGRAInto(const uint8_t* data, size_t data_size,
    ///                             uint8_t* output_buffer, size_t output_buffer_size, int output_stride);
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern unsafe byte* WebPDecodeBGRAInto(
        byte* data, nuint dataSize, byte* outputBuffer, nuint outputBufferSize, int outputStride);

    private static readonly Lazy<bool> s_isAvailable = new(() =>
        NativeLibrary.TryLoad(
            DllName,
            typeof(LibWebP).Assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories,
            out _));

    /// <summary>libwebp.dll がロードできるか（初回のみ判定してキャッシュ）</summary>
    public static bool IsAvailable => s_isAvailable.Value;
}
