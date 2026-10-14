# ImageViewer

Windows 用の軽量な画像ビューア（C# / .NET 10 / WPF）。
**ビューア専用**で、画像ファイルには一切書き込まない方針。

## 必要なもの

- 開発：Visual Studio 2026（.NET 10 SDK 入り）
  - ワークロード「.NET デスクトップ開発」
- 実行：.NET 10 Desktop Runtime（framework-dependent の軽量exeなので）

`ImageViewer.sln` を開いて F5 で起動できる。

## フォルダ構成

```
ImageViewer.sln
native/x64/                   ← libwebp.dll を置く場所（任意）
src/ImageViewer/
  App.xaml(.cs)               起動処理（コマンドライン引数でファイルを開く）
  MainWindow.xaml(.cs)        表示・ズーム・パン・キー操作
  Imaging/
    IImageDecoder.cs          デコーダの共通インターフェース
    WicImageDecoder.cs        WPF標準(WIC)のデコーダ
    WebPImageDecoder.cs       libwebp によるWebPフォールバック
    ImageLoader.cs            デコーダを順に試す（WIC → libwebp）
  Navigation/
    FolderNavigator.cs        同じフォルダ内の画像一覧と前後移動
    NaturalFileNameComparer.cs 自然順ソート（StrCmpLogicalW）
  Interop/
    LibWebP.cs                libwebp.dll の P/Invoke 定義
```

## WebP 対応（二段構え）

1. まず WIC で読む（OS に「WebP 画像拡張機能」があればここで読める）
2. 読めなかったら libwebp.dll で読む

libwebp.dll を `native/x64/` に置くと、ビルド時に出力フォルダへコピーされる。
置かなくてもビルド・実行はできる（そのときは 1. だけで動く）。

DLL の入手例（vcpkg）：

```
vcpkg install libwebp:x64-windows
```

`installed\x64-windows\bin\` にある `libwebp.dll` と、依存DLL（`libsharpyuv.dll` など）を
まとめて `native/x64/` にコピーする。
※ アニメーションWebPは未対応（先頭フレームも出ない）。

## 画面構成

- 上部：ツールバー（左回転 / 右回転 / 全画面）
- 中央：画像表示領域
- 下部：ステータスバー（ファイル名 / 何枚目か / 縦横ピクセル数 / 表示倍率）
- タイトルバーには「ファイル名 - ImageViewer」だけ出す
- 全画面中はツールバーとステータスバーを隠す

## 操作

| 操作 | 動作 |
|---|---|
| Ctrl+O / ドラッグ＆ドロップ / exeへドロップ | 開く（フォルダもOK） |
| → / PageDown / Space / ホイール下 | 次の画像 |
| ← / PageUp / BackSpace / ホイール上 | 前の画像 |
| Home / End | 先頭 / 末尾 |
| Ctrl+ホイール / + / - | ズーム（マウス位置 or 中央基準） |
| F / ダブルクリック | フィット表示 ⇔ 等倍 |
| 1 | 等倍 |
| 左ドラッグ | パン |
| R / L / ツールバー | 右回転 / 左回転（表示のみ。画像を切り替えるとリセット） |
| Enter / ツールバー | 全画面表示の切り替え |
| Esc | 全画面表示の終了 |

## 今後のTODO

- 並び順の切り替え
- 前後の画像の先読み
- 設定の保存（ウィンドウ位置など）
- サムネイル一覧 / GIFアニメ / EXIF表示 / スライドショー
