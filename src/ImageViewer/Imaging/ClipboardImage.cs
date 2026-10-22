using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ImageViewer.Imaging;

/// <summary>
/// 画像をクリップボードに入れる。
/// ふつうのビットマップ（ペイントや Office など、ほとんどのアプリが受け取れる）に加えて、
/// PNG も一緒に入れる（ビットマップでは透明が消えるので。ブラウザや画像編集ソフトは PNG を優先して受け取る）。
/// </summary>
public static class ClipboardImage
{
    /// <summary>PNG を受け取るアプリが見る形式の名前</summary>
    public const string PngFormat = "PNG";

    /// <summary>クリップボードに入れる中身を作る（テストで中身を確かめられるように分けてある）</summary>
    public static DataObject CreateDataObject(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        data.SetData(PngFormat, png, autoConvert: false);

        return data;
    }

    /// <summary>
    /// クリップボードに入れる。アプリを閉じても残るようにする。
    /// ほかのアプリがクリップボードを使っていると ExternalException（COMException）になる。
    /// </summary>
    public static void Copy(BitmapSource image) =>
        Clipboard.SetDataObject(CreateDataObject(image), copy: true);
}
