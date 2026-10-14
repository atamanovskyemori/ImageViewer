using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// パスを受け取って画像を読み込むもの。
/// ImageCache はこれ越しに読み込むので、テストでは偽物に差し替えられる。
/// </summary>
public interface IImageLoader
{
    /// <summary>
    /// 画像を読み込んでデコードする。バックグラウンドスレッドから呼ばれる。
    /// 戻り値は Freeze 済みにすること。表示できなければ <see cref="ImageLoadException"/> を投げる。
    /// </summary>
    BitmapSource Load(string path);
}
