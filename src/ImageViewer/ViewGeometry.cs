using System.Windows;
using System.Windows.Media;

namespace ImageViewer;

/// <summary>
/// 画像の表示位置の計算（ズーム・回転・パン）。画面に触らないので、テストできる。
/// 画像は「等倍・回転なしの大きさ（DIP）」の矩形として扱い、回転 → 拡縮 → オフセットの順に画面へ写す。
/// </summary>
public static class ViewGeometry
{
    /// <summary>回転を反映した大きさ（90° / 270° なら縦横が入れ替わる）</summary>
    public static Size Rotated(Size baseSize, int rotation) =>
        rotation % 180 == 0 ? baseSize : new Size(baseSize.Height, baseSize.Width);

    /// <summary>
    /// 画像（等倍・回転なしの矩形）→ 画面 への変換行列を作る。
    /// 1. 画像の中心を原点に移す → 2. 回転 → 3. 回転後の外接矩形の左上を原点に戻す
    /// → 4. 拡縮 → 5. オフセット分ずらす
    /// </summary>
    /// <param name="offset">回転・拡縮後の画像の外接矩形の左上（画面座標）</param>
    public static Matrix Build(Size baseSize, int rotation, double zoom, Vector offset)
    {
        var rotated = Rotated(baseSize, rotation);

        var m = Matrix.Identity;
        m.Translate(-baseSize.Width / 2, -baseSize.Height / 2);
        m.Rotate(rotation);
        m.Translate(rotated.Width / 2, rotated.Height / 2);
        m.Scale(zoom, zoom);
        m.Translate(offset.X, offset.Y);
        return m;
    }

    /// <summary>
    /// 画面上の点が、画像のどこにあたるかを、画像の大きさに対する割合で返す（回転前の画像の左上が 0、右下が 1）。
    /// 大きさの違う画像でも「同じ場所」を指せるように、割合にする。
    /// </summary>
    public static Point ToImageRatio(Point screen, Size baseSize, int rotation, double zoom, Vector offset)
    {
        if (baseSize.Width <= 0 || baseSize.Height <= 0 || zoom <= 0)
        {
            return new Point(0.5, 0.5);
        }

        var inverse = Build(baseSize, rotation, zoom, offset);
        inverse.Invert();
        var p = inverse.Transform(screen);
        return new Point(p.X / baseSize.Width, p.Y / baseSize.Height);
    }

    /// <summary>画像の ratio の場所（<see cref="ToImageRatio"/> と同じ割合）が、画面の screen に来るオフセットを返す</summary>
    public static Vector OffsetFor(Point ratio, Point screen, Size baseSize, int rotation, double zoom)
    {
        var imagePoint = new Point(ratio.X * baseSize.Width, ratio.Y * baseSize.Height);
        var shown = Build(baseSize, rotation, zoom, default).Transform(imagePoint);
        return screen - shown;
    }
}
