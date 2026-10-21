using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ImageViewer.Interop;

namespace ImageViewer;

/// <summary>
/// 起動を1つにまとめる。2回目以降の起動（ファイルの関連付け・exe へのドロップなど）では、
/// 起動中のアプリにパスを渡して、自分はウィンドウを出さずに終わる。プロセスの起動・ウィンドウ作りを待たないので速い。
/// <list type="bullet">
/// <item>どれが最初の起動かは、名前付きミューテックスで決める（同じユーザー・同じログオンの中だけ）</item>
/// <item>パスは名前付きパイプで渡す（同じユーザーからしかつなげない）</item>
/// <item>exe の場所ごとに別に数える（開発中のビルドと、普段使いの exe が混ざらないように。ini も exe ごとなので）</item>
/// </list>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // 起動中のアプリがパイプを開くまで待つ時間（起動の途中に2回目を起動したとき用）
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    // 1回分の受け取りにかける時間の上限（つないだまま何も送ってこない相手で止まらないように）
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>この exe の場所から決まる名前（ミューテックスとパイプに使う）</summary>
    public static string NameFor(string exeDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDirectory)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"ImageViewer-{hash}";
    }

    /// <summary>
    /// 最初の起動なら、受け付け役になって返す。もう起動中のアプリがあれば null。
    /// </summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + name);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // 前のアプリが異常終了した。持ち主はいなくなっていて、取れている
        }

        return new SingleInstance(mutex, name);
    }

    /// <summary>
    /// 起動中のアプリにパスを渡して、ウィンドウを前に出してもらう。
    /// 渡せなかったら false（起動中のアプリが固まっている・終了中など。そのときは自分で開く）。
    /// </summary>
    /// <param name="path">開くファイル・フォルダのフルパス。null なら前に出すだけ</param>
    public static bool TrySend(string name, string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);

            // 前に出るのは、ユーザーが操作した側（このプロセス）にしか許されていないので、相手に許可を渡す
            if (Kernel32.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverProcessId))
            {
                User32.AllowSetForegroundWindow(serverProcessId);
            }

            var bytes = Encoding.UTF8.GetBytes(path ?? string.Empty);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"起動中のアプリにパスを渡せませんでした: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 2回目以降の起動からパスを受け取り始める。received は裏のスレッドで呼ばれる（null なら前に出すだけ）。
    /// </summary>
    public void StartListening(Action<string?> received)
    {
        _ = Task.Run(() => ListenAsync(received, _stop.Token));
    }

    private async Task ListenAsync(Action<string?> received, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ReceiveTimeout);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);

                received(string.IsNullOrWhiteSpace(text) ? null : text);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return; // 終了
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // 途中で切れた・時間切れ。次の起動を待ち直す
                Debug.WriteLine($"ほかの起動からの受け取りに失敗: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 持っていなかった（別のスレッドから呼ばれた等）。プロセスが終われば放される
        }
        _mutex.Dispose();
    }
}
