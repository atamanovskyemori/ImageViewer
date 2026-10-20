using System.Windows;

namespace ImageViewer.Dialogs;

/// <summary>フォルダの端でどうするか</summary>
public enum FolderEdgeChoice
{
    /// <summary>何もしない</summary>
    Cancel,

    /// <summary>次（前）のフォルダへ移る</summary>
    MoveToFolder,

    /// <summary>同じフォルダの先頭（最後）に戻る</summary>
    Wrap,
}

/// <summary>
/// フォルダの最後の画像で「次へ」、最初の画像で「前へ」としたときに出す確認。
/// 「次（前）のフォルダへ」「フォルダの先頭（最後）に戻る」「キャンセル」から選ぶ。
/// 隣のフォルダが無ければ「次（前）のフォルダへ」は押せない。Esc はキャンセル。
/// </summary>
public partial class FolderEdgeDialog : Window
{
    public FolderEdgeDialog(int direction, string? folderName)
    {
        InitializeComponent();

        bool next = direction > 0;
        string side = next ? "次" : "前";

        MoveButton.Content = $"{side}のフォルダへ";
        WrapButton.Content = next ? "フォルダの先頭に戻る" : "フォルダの最後に戻る";
        MessageText.Text =
            (next ? "このフォルダの最後の画像です。" : "このフォルダの最初の画像です。") + "\n"
            + (folderName is null ? $"{side}のフォルダはありません。" : $"{side}のフォルダ：{folderName}");

        MoveButton.IsEnabled = folderName is not null;

        // Enter で押されるボタン（フォルダを移れるならそれ、無ければ戻る）
        var defaultButton = folderName is not null ? MoveButton : WrapButton;
        defaultButton.IsDefault = true;
        Loaded += (_, _) => defaultButton.Focus();
    }

    public FolderEdgeChoice Choice { get; private set; } = FolderEdgeChoice.Cancel;

    /// <summary>ダイアログを出して、選ばれたものを返す（閉じるボタン・Esc はキャンセル）</summary>
    public static FolderEdgeChoice Ask(Window owner, int direction, string? folderName)
    {
        var dialog = new FolderEdgeDialog(direction, folderName) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = FolderEdgeChoice.MoveToFolder;
        DialogResult = true;
    }

    private void WrapButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = FolderEdgeChoice.Wrap;
        DialogResult = true;
    }
}
