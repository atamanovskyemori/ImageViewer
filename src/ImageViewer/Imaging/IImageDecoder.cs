namespace ImageViewer.Imaging;

/// <summary>
/// 画像デコーダの共通インターフェース。
/// ImageLoader はこれを登録順に試し、最初に成功した結果を使う。
/// 新しい形式（AVIFなど）に対応するときは、これを実装して登録するだけでいい。
/// </summary>
public interface IImageDecoder
{
    /// <summary>ログやエラー表示用の名前</summary>
    string Name { get; }

    /// <summary>このデコーダが今の環境で使えるか（ネイティブDLLの有無など）</summary>
    bool IsAvailable { get; }

    /// <summary>ファイル先頭のバイト列を見て、デコードを試す価値があるか判定する</summary>
    bool CanDecode(ReadOnlySpan<byte> header);

    /// <summary>
    /// 画像をデコードする。バックグラウンドスレッドから呼ばれるので、
    /// 中の BitmapSource は必ず Freeze 済みにすること（UIスレッドに渡すため）。
    /// 失敗したら例外を投げる。
    /// </summary>
    /// <param name="maxPixelSize">
    /// 長い辺がこれを大きく超える静止画は、ここまで縮めて読んでいい（0 なら元の大きさ）。
    /// 縮めたときも <see cref="DecodedImage.PixelWidth"/> などは元の大きさにすること。縮められないデコーダは無視していい
    /// </param>
    DecodedImage Decode(byte[] data, int maxPixelSize);
}
