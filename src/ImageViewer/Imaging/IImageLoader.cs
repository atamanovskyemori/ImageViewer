namespace ImageViewer.Imaging;

/// <summary>
/// パスを受け取って画像を読み込むもの。
/// ImageCache はこれ越しに読み込むので、テストでは偽物に差し替えられる。
/// </summary>
public interface IImageLoader
{
    /// <summary>
    /// 画像を読み込んでデコードする。バックグラウンドスレッドから呼ばれる。
    /// 中の BitmapSource は Freeze 済みにすること。表示できなければ <see cref="ImageLoadException"/> を投げる。
    /// </summary>
    /// <param name="maxPixelSize">長い辺がこれを大きく超える静止画は、縮めて読んでいい（0 なら元の大きさ）</param>
    /// <param name="firstFrame">アニメーションの1コマ目ができた時点で呼ばれる（<see cref="IImageDecoder.Decode"/>）</param>
    DecodedImage Load(string path, int maxPixelSize = 0, Action<DecodedImage>? firstFrame = null);
}
