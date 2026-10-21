using System.IO;
using System.Windows;
using ImageViewer.Settings;

namespace ImageViewer;

public partial class App : Application
{
    // 最初の起動のときだけ持つ（2回目以降の起動からパスを受け取る）
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // exeへのドロップ / ファイル関連付けからの起動では、第1引数にパスが渡される。
        // 起動中のアプリに渡すときは、そちらとカレントフォルダが違うのでフルパスにしておく
        string? path = e.Args.Length > 0 ? ToFullPath(e.Args[0]) : null;

        if (AppSettings.Load(AppSettings.DefaultPath).SingleInstance)
        {
            var name = SingleInstance.NameFor(AppContext.BaseDirectory);
            _singleInstance = SingleInstance.TryAcquire(name);
            if (_singleInstance is null && SingleInstance.TrySend(name, path))
            {
                Shutdown(); // 起動中のウィンドウで開いてもらった
                return;
            }
            // 渡せなかった（起動中のアプリが固まっている等）ときは、ふつうに自分で開く
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _singleInstance?.StartListening(received =>
            Dispatcher.BeginInvoke(() => window.OpenFromAnotherLaunch(received)));

        if (path is not null)
        {
            window.OpenPath(path);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path; // 開くときにエラーを出してもらう
        }
    }
}
