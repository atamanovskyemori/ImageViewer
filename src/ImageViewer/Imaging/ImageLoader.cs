using System.IO;

namespace ImageViewer.Imaging;

/// <summary>画像を表示できなかったときの例外。Message はそのままUIに出せる文言にする。</summary>
public sealed class ImageLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 登録されたデコーダを順番に試して、最初に成功したものを返す。
/// 既定では「アニメーションWebP（libwebpdemux）→ WIC → libwebp」の順。
/// </summary>
public sealed class ImageLoader : IImageLoader
{
    private readonly IReadOnlyList<IImageDecoder> _decoders;

    public ImageLoader(IEnumerable<IImageDecoder> decoders)
    {
        _decoders = decoders.ToArray();
    }

    /// <param name="maxAnimationBytes">1枚のアニメーションに使っていいメモリ。超えたら1枚目だけ表示する</param>
    public static ImageLoader CreateDefault(long maxAnimationBytes = AnimationLimits.DefaultMaxBytes) => new(
    [
        // WIC の WebP コーデックはアニメーションの1コマ目しか返さないので、アニメーションWebPだけ先に取る
        new WebPAnimationDecoder(maxAnimationBytes),
        new WicImageDecoder(maxAnimationBytes),
        new WebPImageDecoder(),
    ]);

    public DecodedImage Load(string path, int maxPixelSize = 0)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImageLoadException($"ファイルを読み込めませんでした。\n{ex.Message}", ex);
        }

        var errors = new List<Exception>();
        foreach (var decoder in _decoders)
        {
            if (!decoder.IsAvailable || !decoder.CanDecode(data))
            {
                continue;
            }

            try
            {
                return decoder.Decode(data, maxPixelSize);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 次のデコーダを試す
                errors.Add(new Exception($"[{decoder.Name}] {ex.Message}", ex));
            }
        }

        throw new ImageLoadException(
            $"この画像は表示できません。\n{Path.GetFileName(path)}",
            new AggregateException(errors));
    }
}
