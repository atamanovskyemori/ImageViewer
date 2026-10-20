using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace ImageViewer.Metadata;

/// <summary>
/// 画像の情報（ファイル・EXIF）を出す右側のパネル。中身は MainWindow から渡す。
/// 右クリックで、出ている情報をテキストとしてコピーできる。
/// </summary>
public partial class InfoPanel : UserControl
{
    private ImageInfo? _info;

    public InfoPanel()
    {
        InitializeComponent();
        ShowMessage("画像が開かれていません");
    }

    /// <summary>情報を出す</summary>
    public void ShowInfo(ImageInfo info)
    {
        _info = info;
        SectionList.ItemsSource = info.Sections;
        SectionList.Visibility = Visibility.Visible;
        MessageText.Visibility = Visibility.Collapsed;
        CopyMenuItem.IsEnabled = true;
        CopyPromptMenuItem.IsEnabled = info.Generation?.Prompt is not null;
        CopyNegativeMenuItem.IsEnabled = info.Generation?.NegativePrompt is not null;
    }

    /// <summary>情報の代わりにメッセージを出す（何も開いていない、読めなかった、など）</summary>
    public void ShowMessage(string message)
    {
        _info = null;
        SectionList.ItemsSource = null;
        SectionList.Visibility = Visibility.Collapsed;
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
        CopyMenuItem.IsEnabled = false;
        CopyPromptMenuItem.IsEnabled = false;
        CopyNegativeMenuItem.IsEnabled = false;
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e) => Copy(_info?.ToText());

    private void CopyPromptMenuItem_Click(object sender, RoutedEventArgs e) => Copy(_info?.Generation?.Prompt);

    private void CopyNegativeMenuItem_Click(object sender, RoutedEventArgs e) => Copy(_info?.Generation?.NegativePrompt);

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // ほかのアプリがクリップボードを使っている。コピーできなくても実害はない
        }
    }
}
