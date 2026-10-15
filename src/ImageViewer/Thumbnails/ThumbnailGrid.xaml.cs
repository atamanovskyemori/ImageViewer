using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageViewer.Imaging;

namespace ImageViewer.Thumbnails;

/// <summary>
/// フォルダ内の画像のサムネイル一覧。
/// <list type="bullet">
/// <item>1行ぶんを1項目にして、仮想化の効くリストに並べる（見えている行の UI しか作らない）</item>
/// <item>サムネイルは見えている行（と上下1行）だけ ThumbnailCache に頼み、離れた行のものは手放す</item>
/// <item>選択は1つだけ。キー操作は MainWindow から <see cref="HandleKey"/> で受け取る（ウィンドウ全体のキー処理と揃えるため）</item>
/// <item>Ctrl+ホイールで大きさを変える。サムネイルは最大の大きさで作ってあるので、作り直しは起きない</item>
/// </list>
/// UIスレッド専用。
/// </summary>
public partial class ThumbnailGrid : UserControl
{
    // 見えている行の上下、何行先まで先に頼んでおくか
    private const int PrefetchRows = 1;

    // 見えている行から何行離れたらサムネイルを手放すか（少し余裕を持たせて、ちょっと戻ったときに読み直さない）
    private const int KeepRows = 3;

    private List<ThumbnailItem> _items = [];
    private List<ThumbnailRow> _rows = [];
    private readonly HashSet<ThumbnailItem> _active = [];

    private ThumbnailCache? _cache;
    private ScrollViewer? _scrollViewer;
    private int _columns;
    private int _size = ThumbnailLayout.DefaultSize;
    private int _selectedIndex = -1;

    // タッチパッドなどの細かいホイール量を貯めて、1段階ぶん（120）たまったら大きさを変える
    private int _zoomWheelDelta;

    public ThumbnailGrid()
    {
        InitializeComponent();
        ApplySizeResources();

        Loaded += (_, _) =>
        {
            _scrollViewer ??= FindDescendant<ScrollViewer>(RowList);
            RequestRealize();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RequestRealize();
            }
            else
            {
                ReleaseAll(); // 隠れている間は、表示用に持っているサムネイルを手放す
            }
        };
    }

    /// <summary>選択が変わったとき（引数は位置）。クリック・キー操作のどちらでも起きる</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>ダブルクリックで画像を開くとき（引数は位置）</summary>
    public event EventHandler<int>? ItemActivated;

    /// <summary>Ctrl+ホイールで大きさが変わったとき</summary>
    public event EventHandler? ThumbnailSizeChanged;

    public ThumbnailCache? Cache
    {
        get => _cache;
        set
        {
            if (ReferenceEquals(_cache, value)) return;
            ReleaseAll();
            _cache = value;
            RequestRealize();
        }
    }

    /// <summary>サムネイルの大きさ（DIP）。<see cref="ThumbnailLayout.Sizes"/> のどれかに丸める</summary>
    public int ThumbnailSize
    {
        get => _size;
        set
        {
            int size = ThumbnailLayout.Normalize(value);
            if (size == _size) return;

            _size = size;
            ApplySizeResources();
            RebuildRows();
        }
    }

    /// <summary>選択中の位置。何も無ければ -1</summary>
    public int SelectedIndex => _selectedIndex;

    public int Count => _items.Count;

    /// <summary>一覧を作り直す。選択は selectedIndex にして、そこまでスクロールする</summary>
    public void SetFiles(IReadOnlyList<string> files, int selectedIndex)
    {
        ReleaseAll();
        _items = files.Select((path, i) => new ThumbnailItem(path, i)).ToList();
        _selectedIndex = -1;
        _columns = 0;

        if (_items.Count > 0)
        {
            _selectedIndex = Math.Clamp(selectedIndex, 0, _items.Count - 1);
            _items[_selectedIndex].IsSelected = true;
        }

        RebuildRows();
    }

    /// <summary>空にする</summary>
    public void Clear() => SetFiles([], -1);

    /// <summary>選択を変えて、見える位置までスクロールする</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        if (index != _selectedIndex)
        {
            if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
            {
                _items[_selectedIndex].IsSelected = false;
            }
            _selectedIndex = index;
            _items[index].IsSelected = true;
            SelectionChanged?.Invoke(this, index);
        }

        ScrollToSelection();
    }

    /// <summary>
    /// 選択を動かすキー（矢印 / PageUp / PageDown / Home / End / Space / BackSpace）を処理する。処理したら true。
    /// </summary>
    public bool HandleKey(Key key)
    {
        ThumbnailMove? move = key switch
        {
            Key.Left or Key.Back => ThumbnailMove.Previous,
            Key.Right or Key.Space => ThumbnailMove.Next,
            Key.Up => ThumbnailMove.Up,
            Key.Down => ThumbnailMove.Down,
            Key.PageUp => ThumbnailMove.PageUp,
            Key.PageDown => ThumbnailMove.PageDown,
            Key.Home => ThumbnailMove.First,
            Key.End => ThumbnailMove.Last,
            _ => null,
        };

        if (move is null)
        {
            return false;
        }

        int target = ThumbnailLayout.Move(_selectedIndex, _items.Count, Math.Max(1, _columns), PageRows, move.Value);
        if (target >= 0)
        {
            Select(target);
        }
        return true;
    }

    /// <summary>表示用に持っているサムネイルを全部手放し、読み込み中のものはキャンセルする</summary>
    public void ReleaseAll()
    {
        foreach (var item in _active.ToList())
        {
            Release(item);
        }
    }

    // ------------------------------------------------------------
    // 行の組み立て・スクロール
    // ------------------------------------------------------------

    private double RowHeight => ThumbnailLayout.TileHeight(_size);

    /// <summary>画面に収まる行数（PageUp / PageDown で動く量）</summary>
    private int PageRows =>
        _scrollViewer is { ViewportHeight: > 0 } sv
            ? Math.Max(1, (int)Math.Floor(sv.ViewportHeight / RowHeight))
            : 1;

    /// <summary>今の幅で1行に何枚入るか。縦スクロールバーが出ても入れ替わらないよう、その幅はいつも引いておく</summary>
    private int ComputeColumns()
    {
        double available = RowList.ActualWidth
            - RowList.Padding.Left - RowList.Padding.Right
            - SystemParameters.VerticalScrollBarWidth;
        return ThumbnailLayout.Columns(available, _size);
    }

    private void RebuildRows()
    {
        if (_items.Count == 0)
        {
            _rows = [];
            _columns = 0;
            RowList.ItemsSource = null;
            return;
        }

        if (RowList.ActualWidth <= 0)
        {
            // まだレイアウトされていない（表示する前）。SizeChanged で組み立てる
            _columns = 0;
            RowList.ItemsSource = null;
            return;
        }

        _columns = ComputeColumns();
        var rows = new List<ThumbnailRow>((_items.Count + _columns - 1) / _columns);
        for (int start = 0, row = 0; start < _items.Count; start += _columns, row++)
        {
            rows.Add(new ThumbnailRow(row, _items.GetRange(start, Math.Min(_columns, _items.Count - start))));
        }

        _rows = rows;
        RowList.ItemsSource = rows;

        // 新しい行がレイアウトされてから、選択した画像が見えるようにする
        Dispatcher.InvokeAsync(ScrollToSelection, DispatcherPriority.Loaded);
    }

    private void ScrollToSelection()
    {
        if (_selectedIndex < 0 || _columns <= 0) return;

        int row = _selectedIndex / _columns;
        if (row < _rows.Count)
        {
            RowList.ScrollIntoView(_rows[row]);
        }
        RequestRealize();
    }

    private void ApplySizeResources()
    {
        Resources["ThumbImageSize"] = (double)_size;
        Resources["ThumbTileWidth"] = ThumbnailLayout.TileWidth(_size);
        Resources["ThumbTileHeight"] = ThumbnailLayout.TileHeight(_size);
    }

    // ------------------------------------------------------------
    // サムネイルを頼む・手放す
    // ------------------------------------------------------------

    private bool _realizePending;

    /// <summary>レイアウトが落ち着いてから、見えている行のサムネイルを頼む（何度呼ばれても1回にまとめる）</summary>
    private void RequestRealize()
    {
        if (_realizePending) return;
        _realizePending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _realizePending = false;
            Realize();
        }, DispatcherPriority.Background);
    }

    private void Realize()
    {
        if (_cache is null || !IsVisible || _rows.Count == 0 || _columns <= 0) return;

        _scrollViewer ??= FindDescendant<ScrollViewer>(RowList);
        if (_scrollViewer is null) return;

        var (first, last) = ThumbnailLayout.VisibleRows(
            _scrollViewer.VerticalOffset, _scrollViewer.ViewportHeight, RowHeight, _rows.Count);
        if (last < first) return;

        // 離れた行のものは手放す
        int keepFirst = first - KeepRows;
        int keepLast = last + KeepRows;
        foreach (var item in _active.ToList())
        {
            int row = item.Index / _columns;
            if (row < keepFirst || row > keepLast)
            {
                Release(item);
            }
        }

        // 見えている行を先に、そのあと上下の行を頼む（同時に作る枚数は ThumbnailCache が絞る）
        for (int row = first; row <= last; row++)
        {
            RequestRow(row);
        }
        for (int i = 1; i <= PrefetchRows; i++)
        {
            RequestRow(last + i);
            RequestRow(first - i);
        }
    }

    private void RequestRow(int row)
    {
        if (row < 0 || row >= _rows.Count) return;

        foreach (var item in _rows[row].Items)
        {
            if (item.Image is null && !item.IsFailed && item.Loading is null)
            {
                _ = LoadAsync(item, _cache!);
            }
        }
    }

    private async Task LoadAsync(ThumbnailItem item, ThumbnailCache cache)
    {
        var cts = new CancellationTokenSource();
        item.Loading = cts;
        _active.Add(item);

        try
        {
            // 持っているものなら完了済みの Task が返るので、ここで止まらずにすぐ出る
            var result = await cache.GetAsync(item.Path, cts.Token);
            if (item.Loading == cts)
            {
                item.Image = result.Image;
                item.IsFailed = result.IsFailed;
            }
        }
        catch (OperationCanceledException)
        {
            // スクロールで見えなくなった・一覧を作り直した
        }
        catch (Exception ex)
        {
            // 投げっぱなしで呼んでいるので、ここで拾わないと誰にも見られずに消える
            Debug.WriteLine(ex);
            if (item.Loading == cts)
            {
                item.IsFailed = true;
            }
        }
        finally
        {
            if (item.Loading == cts)
            {
                item.Loading = null;
            }
            cts.Dispose();
        }
    }

    private void Release(ThumbnailItem item)
    {
        item.Loading?.Cancel();
        item.Loading = null;
        item.Image = null;
        item.IsFailed = false;
        _active.Remove(item);
    }

    // ------------------------------------------------------------
    // イベント
    // ------------------------------------------------------------

    private void RowList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scrollViewer ??= e.OriginalSource as ScrollViewer;
        RequestRealize();
    }

    private void RowList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _items.Count == 0)
        {
            RequestRealize();
            return;
        }

        // 1行の枚数が変わるときだけ組み直す（高さだけの変化はスクロール範囲が変わるだけ）
        if (ComputeColumns() != _columns)
        {
            RebuildRows();
        }
        else
        {
            RequestRealize();
        }
    }

    private void RowList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _zoomWheelDelta += e.Delta;
            while (Math.Abs(_zoomWheelDelta) >= Mouse.MouseWheelDeltaForOneLine)
            {
                int direction = Math.Sign(_zoomWheelDelta);
                _zoomWheelDelta -= direction * Mouse.MouseWheelDeltaForOneLine;

                int before = _size;
                ThumbnailSize = ThumbnailLayout.StepSize(_size, direction);
                if (_size != before)
                {
                    ThumbnailSizeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            e.Handled = true;
            return;
        }

        // 既定のスクロール量（3行 × 16px くらい）だとサムネイルには少なすぎるので、1段階で1行ぶん動かす
        if (_scrollViewer is { } sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * RowHeight);
            e.Handled = true;
        }
    }

    private void RowList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // スクロールバーなどタイル以外の場所はそのまま通す
        if (FindItem(e.OriginalSource) is not { } item) return;

        Select(item.Index);
        if (e.ClickCount == 2)
        {
            ItemActivated?.Invoke(this, item.Index);
        }
        e.Handled = true;
    }

    private static ThumbnailItem? FindItem(object source) => source switch
    {
        FrameworkElement { DataContext: ThumbnailItem item } => item,
        FrameworkContentElement { DataContext: ThumbnailItem item } => item,
        _ => null,
    };

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }
            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }
}
