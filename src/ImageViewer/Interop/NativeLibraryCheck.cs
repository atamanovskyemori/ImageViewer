using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ImageViewer.Interop;

/// <summary>
/// WebP 用の DLL（libsharpyuv → libwebp → libwebpdemux の順に依存）が読めるかを調べて、読めなければ理由を返す。
/// DLL が読めなくても WIC だけで動くので、エラーは出さずに黙ってしまう。それでは原因が分からないので、
/// 起動時に1回だけ調べて、ステータスバーに出す。
/// </summary>
internal static class NativeLibraryCheck
{
    // 依存される側から順に（libwebp は libsharpyuv を、libwebpdemux は libwebp を使う）
    private static readonly string[] WebPLibraries = ["libsharpyuv.dll", "libwebp.dll", "libwebpdemux.dll"];

    /// <summary>
    /// 問題が無ければ null。あれば、短い説明（ステータスバー用）と詳しい説明（ツールチップ用）を返す。
    /// </summary>
    public static (string Summary, string Detail)? CheckWebP() => CheckWebP(AppContext.BaseDirectory);

    /// <param name="directory">DLL を探すフォルダ（exe と同じ場所）</param>
    public static (string Summary, string Detail)? CheckWebP(string directory)
    {
        // 1. そもそも置いてあるか
        var missing = WebPLibraries.Where(name => !File.Exists(Path.Combine(directory, name))).ToList();
        if (missing.Count == WebPLibraries.Length)
        {
            return ("WebP DLL なし",
                "WebP 用の DLL が exe と同じフォルダにありません。\n"
                + "WebP は OS の「WebP 画像拡張機能」があるときだけ表示でき、アニメーション WebP は1コマ目だけになります。\n"
                + $"必要な DLL：{string.Join(" / ", WebPLibraries)}");
        }
        if (missing.Count > 0)
        {
            return ($"WebP DLL 不足：{string.Join(", ", missing)}",
                $"次の DLL が exe と同じフォルダにありません：{string.Join(", ", missing)}\n"
                + "3つは依存し合っているので、1つでも欠けると WebP の DLL は全部使えません"
                + "（libsharpyuv → libwebp → libwebpdemux の順に依存）。\n"
                + "WebP は OS の拡張機能だけで表示し、アニメーション WebP は1コマ目だけになります。");
        }

        // 2. 置いてあるのに読めない（64ビット版でない、Visual C++ ランタイムが無い、など）
        foreach (var name in WebPLibraries)
        {
            var path = Path.Combine(directory, name);
            try
            {
                // 一度読めば、あとの P/Invoke も同じものを使う（解放しない）
                NativeLibrary.Load(path);
            }
            catch (BadImageFormatException)
            {
                return ($"WebP DLL が 64 ビット版ではない：{name}",
                    $"{name} が 64 ビット（x64）版ではありません。このアプリは x64 で動くので、x64 版の DLL に入れ替えてください。");
            }
            catch (DllNotFoundException ex)
            {
                return ($"WebP DLL を読めない：{name}",
                    $"{name} を読み込めませんでした。\n"
                    + "Visual C++ 再頒布可能パッケージ（VCRUNTIME140.dll）が入っていないか、DLL が壊れている可能性があります。\n"
                    + $"（{ex.Message}）");
            }
        }

        return null;
    }
}
