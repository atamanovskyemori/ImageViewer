using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace ImageViewer.Thumbnails;

/// <summary>
/// 一覧の1枚。画面に見えている間だけ Image を持つ（見えなくなったら手放して、メモリは ThumbnailCache の上限に任せる）。
/// UIスレッド専用。
/// </summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private bool _isFailed;
    private bool _isSelected;

    public ThumbnailItem(string path, int index)
    {
        Path = path;
        Index = index;
        FileName = System.IO.Path.GetFileName(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    /// <summary>一覧の中の位置（FolderNavigator の並びと同じ）</summary>
    public int Index { get; }

    public string FileName { get; }

    /// <summary>サムネイル。まだ無い・見えていないときは null</summary>
    public BitmapSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    /// <summary>読めなかったか（代わりの印を出す）</summary>
    public bool IsFailed
    {
        get => _isFailed;
        set => Set(ref _isFailed, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>読み込み中ならそのキャンセル用。ThumbnailGrid だけが触る</summary>
    internal CancellationTokenSource? Loading { get; set; }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 一覧の1行。WPF の WrapPanel は仮想化できない（見えない分まで全部作られる）ので、
/// 「1行ぶんのサムネイル」を1項目にして、仮想化が効く縦のリストに並べる。
/// </summary>
public sealed class ThumbnailRow(int index, IReadOnlyList<ThumbnailItem> items)
{
    public int Index { get; } = index;

    public IReadOnlyList<ThumbnailItem> Items { get; } = items;
}
