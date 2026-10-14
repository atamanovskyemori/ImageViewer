using System.Windows;

namespace ImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // exeへのドロップ / ファイル関連付けからの起動では、第1引数にパスが渡される
        if (e.Args.Length > 0)
        {
            window.OpenPath(e.Args[0]);
        }
    }
}
