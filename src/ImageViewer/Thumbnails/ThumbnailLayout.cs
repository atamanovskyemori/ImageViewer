namespace ImageViewer.Thumbnails;

/// <summary>一覧での選択の動かし方</summary>
public enum ThumbnailMove
{
    Previous,
    Next,
    Up,
    Down,
    PageUp,
    PageDown,
    First,
    Last,
}

/// <summary>
/// サムネイル一覧の大きさ・並べ方・選択移動の計算。UI に依存しない（テストできるように分けている）。
/// 大きさはすべて DIP（1/96インチ）。
/// </summary>
public static class ThumbnailLayout
{
    /// <summary>選べるサムネイルの大きさ（画像を入れる正方形の一辺）。Ctrl+ホイールでこの順に切り替える</summary>
    public static readonly IReadOnlyList<int> Sizes = [64, 80, 96, 128, 160, 192, 224, 256];

    public const int DefaultSize = 160;

    public static int MinSize => Sizes[0];

    public static int MaxSize => Sizes[^1];

    /// <summary>
    /// タイル1枚のうち、画像以外の部分。ThumbnailGrid.xaml のタイルの作りと合わせること。
    /// 横：外側の余白 3 + 枠 2 + 内側の余白 3 を左右に。縦：それに加えてファイル名の欄（高さ 18 + 上の余白 2）
    /// </summary>
    public const double ChromeWidth = 2 * (3 + 2 + 3);

    public const double ChromeHeight = 2 * (3 + 2 + 3) + 18 + 2;

    public static double TileWidth(int size) => size + ChromeWidth;

    public static double TileHeight(int size) => size + ChromeHeight;

    /// <summary>ini などから来た値を、選べる大きさのうち一番近いものにする</summary>
    public static int Normalize(int size)
    {
        int best = Sizes[0];
        foreach (int candidate in Sizes)
        {
            if (Math.Abs(candidate - size) < Math.Abs(best - size))
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>大きさを1段階変える（direction: +1 で大きく / -1 で小さく）。端では止まる</summary>
    public static int StepSize(int size, int direction)
    {
        int index = IndexOf(Normalize(size));
        int next = Math.Clamp(index + Math.Sign(direction), 0, Sizes.Count - 1);
        return Sizes[next];
    }

    /// <summary>幅 availableWidth に何枚並ぶか（最低1枚）</summary>
    public static int Columns(double availableWidth, int size)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return 1;
        }
        return Math.Max(1, (int)Math.Floor(availableWidth / TileWidth(size)));
    }

    /// <summary>
    /// スクロール位置から、画面に見えている行の範囲を返す（両端を含む）。見えている行が無ければ Last &lt; First。
    /// </summary>
    public static (int First, int Last) VisibleRows(double offset, double viewportHeight, double rowHeight, int rowCount)
    {
        if (rowCount <= 0 || rowHeight <= 0 || viewportHeight <= 0)
        {
            return (0, -1);
        }

        int first = (int)Math.Floor(Math.Max(0, offset) / rowHeight);
        int last = (int)Math.Ceiling((Math.Max(0, offset) + viewportHeight) / rowHeight) - 1;
        first = Math.Clamp(first, 0, rowCount - 1);
        last = Math.Clamp(last, first, rowCount - 1);
        return (first, last);
    }

    /// <summary>
    /// 選択を動かした先の位置を返す。
    /// 上下は同じ列へ。下に画像が無いときは、最後の行がまだ下にあれば最後の画像へ（エクスプローラーと同じ）。
    /// 何も選んでいなければ先頭を選ぶ。画像が無ければ -1。
    /// </summary>
    /// <param name="pageRows">PageUp / PageDown で動く行数（画面に収まる行数）</param>
    public static int Move(int index, int count, int columns, int pageRows, ThumbnailMove move)
    {
        if (count <= 0)
        {
            return -1;
        }
        if (index < 0 || index >= count)
        {
            return 0;
        }

        columns = Math.Max(1, columns);
        pageRows = Math.Max(1, pageRows);

        int row = index / columns;
        int column = index % columns;
        int lastRow = (count - 1) / columns;

        switch (move)
        {
            case ThumbnailMove.Previous:
                return Math.Max(0, index - 1);

            case ThumbnailMove.Next:
                return Math.Min(count - 1, index + 1);

            case ThumbnailMove.Up:
                return row > 0 ? index - columns : index;

            case ThumbnailMove.Down:
                if (index + columns < count) return index + columns;
                return row < lastRow ? count - 1 : index;

            case ThumbnailMove.PageUp:
            {
                int targetRow = Math.Max(0, row - pageRows);
                return targetRow * columns + column;
            }

            case ThumbnailMove.PageDown:
            {
                int targetRow = Math.Min(lastRow, row + pageRows);
                return targetRow == row ? index : Math.Min(count - 1, targetRow * columns + column);
            }

            case ThumbnailMove.First:
                return 0;

            case ThumbnailMove.Last:
                return count - 1;

            default:
                return index;
        }
    }

    private static int IndexOf(int size)
    {
        for (int i = 0; i < Sizes.Count; i++)
        {
            if (Sizes[i] == size) return i;
        }
        return 0;
    }
}
