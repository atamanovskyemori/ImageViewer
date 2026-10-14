# ImageViewer

Windows 用の軽量な画像ビューア（C# / .NET 10 / WPF）。
**ビューア専用**で、画像ファイルには一切書き込まない方針。

## 必要なもの

- 開発：Visual Studio 2026（.NET 10 SDK 入り）
  - ワークロード「.NET デスクトップ開発」
- 実行：.NET 10 Desktop Runtime（framework-dependent の軽量exeなので）

`ImageViewer.sln` を開いて F5 で起動できる。
exe は x64 固定（同梱する libwebp.dll に合わせている）。

テストは「テスト エクスプローラー」から実行する（MSTest）。

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
    IImageLoader.cs           読み込み処理のインターフェース（テストで差し替えるため）
    ImageLoader.cs            デコーダを順に試す（WIC → libwebp）
    ImageCache.cs             デコード済み画像のキャッシュと前後の先読み
  Navigation/
    FolderNavigator.cs        同じフォルダ内の画像一覧と前後移動
    NaturalFileNameComparer.cs 自然順ソート（StrCmpLogicalW）
    FileSort.cs               並び順の種類と比較（名前 / 更新日時 / サイズ）
  Interop/
    LibWebP.cs                libwebp.dll の P/Invoke 定義
tests/ImageViewer.Tests/
  Imaging/ImageCacheTests.cs  先読み・キャッシュのテスト
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

## EXIF の向き

JPEG / TIFF は EXIF の Orientation を見て、撮影したときの向きで表示する（スマホの縦写真など）。
ファイルは書き換えない。縦横ピクセル数も向きを直した後の値を出す。
R / L での回転は、その上にさらに表示だけ回す。

## 画面構成

- 上部：ツールバー（左回転 / 右回転 / 全画面 / 並び順 / 昇順・降順）
- 中央：画像表示領域
- 下部：ステータスバー（ファイル名 / 何枚目か / 縦横ピクセル数 / 表示倍率）
  - 次の画像を読み込んでいる間は、縦横ピクセル数の欄に「読み込み中…」と出す
- タイトルバーには「ファイル名 - ImageViewer」だけ出す
- 全画面中はツールバーとステータスバーを隠す

## 並び順

- 名前（自然順） / 更新日時 / サイズ をツールバーのボタンで選ぶ（選択中のボタンはハイライト＋太字）
- 昇順・降順はその隣のボタンで切り替え（今の状態をアイコンと文字で表示）
- 主キーが同じものは名前の自然順で並べる
- 切り替えても表示中の画像はそのまま（読み直さない）。一覧の順番と「何枚目か」だけ変わる
- フォルダを開いたときは先頭（並び順の1枚目）を表示する

## 先読み

表示中の画像の前後を裏でデコードしておき、送りを速くする。

- 進んでいる方向に2枚、逆方向に1枚（現在の画像と合わせて最大4枚をメモリに持つ）
- 表示する画像のデコードが最優先。先読みは同時に1枚ずつ
- 先読みが終わっている画像は、そのまま表示に使う（読み込み途中なら、その完了を待つ）
- 順番待ちの先読みが表示対象になったら、待たずにすぐ読む
- 範囲外になった画像はキャッシュから捨てる
- 同じ画像を開き直すと読み直す（ファイルの更新を反映するため）

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

- 設定の保存（ウィンドウ位置など）
- サムネイル一覧 / GIFアニメ / EXIF情報の表示 / スライドショー
