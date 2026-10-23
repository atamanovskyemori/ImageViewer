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

    /// <summary>
    /// 画面（viewport の大きさの矩形）に見えている、画像の範囲を返す（画像のピクセル座標。回転前）。
    /// 回転しているときは、見えている四隅を囲む範囲。見えていなければ <see cref="Int32Rect.Empty"/>
    /// </summary>
    /// <param name="pixelWidth">画像の幅（ピクセル）。baseSize は、この画像を等倍で出したときの大きさ（DIP）</param>
    public static Int32Rect VisiblePixels(
        Size viewport, Size baseSize, int rotation, double zoom, Vector offset, int pixelWidth, int pixelHeight)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || baseSize.Width <= 0 || baseSize.Height <= 0
            || pixelWidth <= 0 || pixelHeight <= 0 || zoom <= 0)
        {
            return Int32Rect.Empty;
        }

        var inverse = Build(baseSize, rotation, zoom, offset);
        inverse.Invert();
        Point[] corners = [new(0, 0), new(viewport.Width, 0), new(0, viewport.Height), new(viewport.Width, viewport.Height)];
        inverse.Transform(corners);

        // DIP（等倍・回転なし）→ 画像のピクセル。回転の計算で出る小さな誤差（1799.9999… など）で、
        // 1ピクセル広がらないよう、少しだけ内側に寄せてから切り捨て・切り上げる
        const double Epsilon = 1e-6;
        double sx = pixelWidth / baseSize.Width;
        double sy = pixelHeight / baseSize.Height;
        int left = (int)Math.Floor(Math.Max(0, corners.Min(p => p.X) * sx + Epsilon));
        int top = (int)Math.Floor(Math.Max(0, corners.Min(p => p.Y) * sy + Epsilon));
        int right = (int)Math.Ceiling(Math.Min(pixelWidth, corners.Max(p => p.X) * sx - Epsilon));
        int bottom = (int)Math.Ceiling(Math.Min(pixelHeight, corners.Max(p => p.Y) * sy - Epsilon));

        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
    }

    /// <summary>範囲を、幅・高さの margin 倍ずつ上下左右に広げる（画像の外にははみ出さない）</summary>
    public static Int32Rect Expand(Int32Rect rect, double margin, int pixelWidth, int pixelHeight)
    {
        int dx = (int)Math.Ceiling(rect.Width * margin);
        int dy = (int)Math.Ceiling(rect.Height * margin);
        int left = Math.Max(0, rect.X - dx);
        int top = Math.Max(0, rect.Y - dy);
        int right = Math.Min(pixelWidth, rect.X + rect.Width + dx);
        int bottom = Math.Min(pixelHeight, rect.Y + rect.Height + dy);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>outer が inner をすっぽり含むか（どちらかが空なら false）</summary>
    public static bool Contains(Int32Rect outer, Int32Rect inner) =>
        !outer.IsEmpty && !inner.IsEmpty
        && inner.X >= outer.X && inner.Y >= outer.Y
        && inner.X + inner.Width <= outer.X + outer.Width
        && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>画像の ratio の場所（<see cref="ToImageRatio"/> と同じ割合）が、画面の screen に来るオフセットを返す</summary>
    public static Vector OffsetFor(Point ratio, Point screen, Size baseSize, int rotation, double zoom)
    {
        var imagePoint = new Point(ratio.X * baseSize.Width, ratio.Y * baseSize.Height);
        var shown = Build(baseSize, rotation, zoom, default).Transform(imagePoint);
        return screen - shown;
    }
}
