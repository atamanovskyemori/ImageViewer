using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>画像の一部を切り出す</summary>
public static class BitmapRegion
{
    /// <summary>
    /// rect の範囲のピクセルを写して、新しい画像（Freeze 済み）にする。
    /// CroppedBitmap と違って元の画像とつながりが無いので、元の画像を裏のスレッドで読んだもの（そのデコーダが
    /// 別のスレッドのもの）でも、どのスレッドからでも使える。ピクセル形式とパレットは元の画像と同じ
    /// </summary>
    public static BitmapSource Copy(BitmapSource source, Int32Rect rect)
    {
        int bitsPerPixel = source.Format.BitsPerPixel;
        int stride = (rect.Width * bitsPerPixel + 7) / 8;
        var pixels = new byte[(long)stride * rect.Height];
        source.CopyPixels(rect, pixels, stride, 0);

        var copy = BitmapSource.Create(rect.Width, rect.Height, 96, 96, source.Format, source.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }
}
