using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// libwebp.dll の必要最小限の P/Invoke 定義（decode.h）。
/// メモリは C# 側で確保した配列に直接書き込ませる（WebPDecodeBGRAInto / WebPDecode の外部メモリ）ので、
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

    // ------------------------------------------------------------
    // 縮めながらデコードする（WebPDecoderConfig の use_scaling）
    // 構造体は decode.h（libwebp 1.x）と同じ並び。x64 で WebPDecoderConfig は 240 バイト
    // ------------------------------------------------------------

    /// <summary>WEBP_DECODER_ABI_VERSION。上位8ビット（メジャー）が DLL と合っていれば使える</summary>
    public const int DecoderAbiVersion = 0x0210;

    /// <summary>WEBP_CSP_MODE の MODE_BGRA（非乗算済みアルファ）</summary>
    public const int ModeBgra = 3;

    /// <summary>VP8StatusCode の VP8_STATUS_OK</summary>
    public const int StatusOk = 0;

    /// <summary>x64 での sizeof(WebPDecoderConfig)。違ったら並びがずれているので使わない</summary>
    public const int ExpectedDecoderConfigSize = 240;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WebPBitstreamFeatures
    {
        public int Width;
        public int Height;
        public int HasAlpha;
        public int HasAnimation;
        public int Format;
        public fixed uint Pad[5];
    }

    /// <summary>
    /// WebPDecBuffer。u は RGBA と YUVA の共用体（大きいほうの YUVA が 80 バイト）なので、
    /// 使う RGBA の部分（24 バイト）だけ名前を付けて、残りは埋め草にする
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WebPDecBuffer
    {
        public int Colorspace;
        public int Width;
        public int Height;
        public int IsExternalMemory;
        public byte* Rgba;
        public int Stride;
        public nuint Size;
        public fixed byte UnionRest[80 - 24];
        public fixed uint Pad[4];
        public byte* PrivateMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WebPDecoderOptions
    {
        public int BypassFiltering;
        public int NoFancyUpsampling;
        public int UseCropping;
        public int CropLeft;
        public int CropTop;
        public int CropWidth;
        public int CropHeight;
        public int UseScaling;
        public int ScaledWidth;
        public int ScaledHeight;
        public int UseThreads;
        public int DitheringStrength;
        public int Flip;
        public int AlphaDitheringStrength;
        public fixed uint Pad[5];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WebPDecoderConfig
    {
        public WebPBitstreamFeatures Input;
        public WebPDecBuffer Output;
        public WebPDecoderOptions Options;
    }

    /// <summary>設定を既定値で初期化する。ABI が合わなければ 0 を返す</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern unsafe int WebPInitDecoderConfigInternal(WebPDecoderConfig* config, int abiVersion);

    /// <summary>設定に従ってデコードする。成功すると VP8_STATUS_OK（0）を返す</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern unsafe int WebPDecode(byte* data, nuint dataSize, WebPDecoderConfig* config);

    /// <summary>デコーダが確保したメモリを解放する（出力先を C# 側で渡したときは何もしない）</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    public static extern unsafe void WebPFreeDecBuffer(WebPDecBuffer* buffer);

    private static readonly Lazy<bool> s_isAvailable = new(() =>
        NativeLibrary.TryLoad(
            DllName,
            typeof(LibWebP).Assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories,
            out _));

    /// <summary>libwebp.dll がロードできるか（初回のみ判定してキャッシュ）</summary>
    public static bool IsAvailable => s_isAvailable.Value;
}
