using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using ImageViewer.Imaging;
using ImageViewer.Interop;

namespace ImageViewer.Metadata;

/// <summary>
/// 情報パネル用に、ファイルの情報と EXIF を読む。画像のデコードはせず、ヘッダとメタデータだけ読むので軽い。
/// バックグラウンドスレッドから呼ぶ。読めなかった部分は省いて、読めた分だけ返す（例外は投げない）。
/// </summary>
public static class ImageInfoLoader
{
    // WebP の縦横はファイル先頭のヘッダにあるので、ここまで読めば足りる
    private const int WebPHeaderBytes = 4096;

    public static ImageInfo Load(string path)
    {
        long? length = null;
        DateTime? lastWriteTime = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                length = info.Length;
                lastWriteTime = info.LastWriteTime;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消された・読めない。ファイル名だけでも出す
        }

        string? format = null;
        int? width = null, height = null;
        bool appliesOrientation = false;
        ExifData? exif = null;
        IReadOnlyList<PngText>? pngTexts = null;
        GenerationInfo? generation = null;

        try
        {
            // 表示中のファイルを他のアプリが書き換え・削除できるように、共有して開く
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var header = new byte[32];
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var kind = Detect(header.AsSpan(0, read));

            (width, height, int frames) = ReadPixelSize(stream, kind);
            format = FormatName(kind, path, header.AsSpan(0, read), frames);
            appliesOrientation = kind is ImageKind.Jpeg or ImageKind.Tiff;

            exif = ExifReader.Read(stream);

            // 画像生成AIの生成情報。PNG はテキストチャンク "parameters"（WebUI）か "prompt"（ComfyUI）、
            // JPEG / WebP は EXIF の UserComment に入っている
            if (kind == ImageKind.Png)
            {
                pngTexts = PngTextReader.Read(stream);
                var parameters = pngTexts.FirstOrDefault(t => string.Equals(t.Keyword, "parameters", StringComparison.OrdinalIgnoreCase));
                generation = GenerationInfo.Parse(parameters?.Text, GenerationSources.PngParameters);

                if (generation is null)
                {
                    var comfyPrompt = pngTexts.FirstOrDefault(t => string.Equals(t.Keyword, "prompt", StringComparison.Ordinal));
                    generation = GenerationInfo.ParseComfyUI(comfyPrompt?.Text);
                }
            }

            if (generation is null
                && GenerationInfo.DecodeUserComment(exif?.Get(ExifIfd.Exif, ExifTags.UserComment)?.Bytes) is { } comment)
            {
                // カメラのふつうのコメントをプロンプトと間違えないよう、「Steps: …」の設定の行があるときだけ生成情報とみなす
                var parsed = GenerationInfo.Parse(comment, GenerationSources.ExifUserComment);
                if (parsed is { Parameters.Count: > 0 })
                {
                    generation = parsed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"情報を読めませんでした: {path}: {ex.Message}");
        }

        var facts = new FileFacts(path, length, lastWriteTime, format, width, height, appliesOrientation);
        return ImageInfoBuilder.Build(facts, exif, generation, pngTexts);
    }

    private enum ImageKind
    {
        Unknown,
        Jpeg,
        Png,
        Gif,
        Bmp,
        Tiff,
        WebP,
        Ico,
        JpegXr,
    }

    private static ImageKind Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ImageKind.Jpeg;
        if (h.Length >= 8 && h[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageKind.Png;
        if (h.Length >= 6 && (h[..6].SequenceEqual("GIF87a"u8) || h[..6].SequenceEqual("GIF89a"u8))) return ImageKind.Gif;
        if (h.Length >= 2 && h[..2].SequenceEqual("BM"u8)) return ImageKind.Bmp;
        if (h.Length >= 4 && (h[..4].SequenceEqual("II*\0"u8) || h[..4].SequenceEqual("MM\0*"u8))) return ImageKind.Tiff;
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8)) return ImageKind.WebP;
        if (h.Length >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0) return ImageKind.Ico;
        if (h.Length >= 3 && h[0] == 'I' && h[1] == 'I' && h[2] == 0xBC) return ImageKind.JpegXr;
        return ImageKind.Unknown;
    }

    /// <summary>縦横ピクセル数とコマ数（GIF のアニメーション判定用）。WIC で読めなければ WebP だけ libwebp で読む</summary>
    private static (int? Width, int? Height, int Frames) ReadPixelSize(Stream stream, ImageKind kind)
    {
        try
        {
            stream.Position = 0;

            // CacheOption.None なので、ヘッダだけ読んでピクセルはデコードしない
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight, decoder.Frames.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // WIC が対応していない（WebP 拡張機能が無いなど）
        }

        if (kind == ImageKind.WebP && LibWebP.IsAvailable)
        {
            stream.Position = 0;
            var head = new byte[WebPHeaderBytes];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (TryGetWebPSize(head, read, out int w, out int h))
            {
                return (w, h, 1);
            }
        }

        return (null, null, 1);
    }

    private static unsafe bool TryGetWebPSize(byte[] data, int length, out int width, out int height)
    {
        int w, h;
        fixed (byte* p = data)
        {
            bool ok = LibWebP.WebPGetInfo(p, (nuint)length, &w, &h) != 0;
            width = w;
            height = h;
            return ok && w > 0 && h > 0;
        }
    }

    private static string FormatName(ImageKind kind, string path, ReadOnlySpan<byte> header, int frames) => kind switch
    {
        ImageKind.Jpeg => "JPEG",
        ImageKind.Png => "PNG",
        ImageKind.Gif => frames > 1 ? $"GIF（アニメーション {frames}コマ）" : "GIF",
        ImageKind.Bmp => "BMP",
        ImageKind.Tiff => "TIFF",
        ImageKind.WebP => WebPAnimationDecoder.IsAnimatedWebP(header) ? "WebP（アニメーション）" : "WebP",
        ImageKind.Ico => "ICO",
        ImageKind.JpegXr => "JPEG XR",
        _ => Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "不明",
    };
}
