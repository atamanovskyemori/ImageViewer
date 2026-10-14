using System.IO;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>画像を表示できなかったときの例外。Message はそのままUIに出せる文言にする。</summary>
public sealed class ImageLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 登録されたデコーダを順番に試して、最初に成功したものを返す。
/// 既定では WIC → libwebp の二段構え。
/// </summary>
public sealed class ImageLoader
{
    private readonly IReadOnlyList<IImageDecoder> _decoders;

    public ImageLoader(IEnumerable<IImageDecoder> decoders)
    {
        _decoders = decoders.ToArray();
    }

    public static ImageLoader CreateDefault() => new(
    [
        new WicImageDecoder(),
        new WebPImageDecoder(),
    ]);

    public Task<BitmapSource> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(path), cancellationToken);

    public BitmapSource Load(string path)
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
                return decoder.Decode(data);
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
