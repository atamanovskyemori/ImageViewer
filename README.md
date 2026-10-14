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
native/x64/                   ← libwebp 系の DLL 3つ（リポジトリに含めている）
src/ImageViewer/
  App.xaml(.cs)               起動処理（コマンドライン引数でファイルを開く）
  MainWindow.xaml(.cs)        表示・ズーム・パン・キー操作
  Animation/
    AnimationTimeline.cs      経過時間から「今どのコマか」を決める（ループ回数・コマ送り）
    AnimationPlayer.cs        タイマーで再生する（一時停止・最小化中は停止）
  Imaging/
    IImageDecoder.cs          デコーダの共通インターフェース
    DecodedImage.cs           デコード結果（静止画、またはアニメーションの全コマ）
    AnimationLimits.cs        アニメーションの決めごと（メモリ上限・表示時間の補正）
    WicImageDecoder.cs        WPF標準(WIC)のデコーダ（2コマ以上のGIFは GifAnimation へ）
    GifAnimation.cs           GIF のメタデータを読んでアニメーションにする
    GifFrameComposer.cs       GIF のコマを重ねて合成する（Disposal 対応）
    WebPAnimationDecoder.cs   libwebpdemux によるアニメーションWebP
    WebPImageDecoder.cs       libwebp によるWebPフォールバック（静止画）
    IImageLoader.cs           読み込み処理のインターフェース（テストで差し替えるため）
    ImageLoader.cs            デコーダを順に試す（アニメWebP → WIC → libwebp）
    ImageCache.cs             デコード済み画像のキャッシュと前後の先読み
  Navigation/
    FolderNavigator.cs        同じフォルダ内の画像一覧と前後移動
    NaturalFileNameComparer.cs 自然順ソート（StrCmpLogicalW）
    FileSort.cs               並び順の種類と比較（名前 / 更新日時 / サイズ）
  Settings/
    IniFile.cs                INI ファイルの読み書き（UTF-8）
    AppSettings.cs            設定の中身と ImageViewer.ini への保存・読み込み
  Interop/
    LibWebP.cs                libwebp.dll の P/Invoke 定義
    LibWebPDemux.cs           libwebpdemux.dll の P/Invoke 定義（アニメーションWebP）
    User32.cs                 ウィンドウ位置の取得・復元（Get/SetWindowPlacement）
tests/ImageViewer.Tests/
  Imaging/                    先読み・キャッシュ、GIF合成、形式の判定などのテスト
  Animation/                  再生タイミングのテスト
  Settings/                   INI・設定の読み書きのテスト
```

## WebP 対応（二段構え）

1. アニメーションWebP は libwebpdemux.dll で読む
2. 静止画は、まず WIC で読む（OS に「WebP 画像拡張機能」があればここで読める）
3. 読めなかったら libwebp.dll で読む

必要な DLL は `native/x64/` に入れてリポジトリに含めている。clone してそのままビルド・実行できる。
ビルド時に出力フォルダへコピーされる（csproj が `native/x64/*.dll` を全部コピーする）。

### 同梱している DLL（3つセット）

| DLL | 役割 | 依存先 |
|---|---|---|
| `libwebp.dll` | WebP のデコード本体 | `libsharpyuv.dll` |
| `libwebpdemux.dll` | アニメーションWebP のコマ取り出し | `libwebp.dll` |
| `libsharpyuv.dll` | 色変換（libwebp が内部で使う） | なし |

**3つとも必要。** 入れ替えや削除をするときは、必ず3つまとめて扱う。
1つでも欠けると、依存している DLL が連鎖して読み込めなくなる。
たとえば `libsharpyuv.dll` が無いと `libwebp.dll` が読めず、それに依存する `libwebpdemux.dll` も読めない。
このときエラーは出ず、DLL が無いときと同じ動き（静止画は WIC だけ、アニメーションWebP は1コマ目だけ静止画）になるので注意。

どれも x64 版（exe が x64 固定なので）。実行には Visual C++ 再頒布可能パッケージ（`VCRUNTIME140.dll`）も必要だが、たいていの PC には入っている。

DLL が無くてもビルド・実行はできるので、外して使うこともできる（そのときは上の 2. だけで動く）。

### ライセンス

libwebp は BSD ライセンス。DLL を含めて配布するときは、ライセンス文（libwebp の `COPYING`）も一緒に入れること。
`native/x64/COPYING` に置いてあり、ビルド時に出力フォルダへ `libwebp-COPYING.txt` という名前でコピーされる。

### DLL を更新・入れ替えるとき（vcpkg の例）

```
vcpkg install libwebp:x64-windows
```

`installed\x64-windows\bin\` にある上の3つを、まとめて `native/x64/` に上書きコピーして、コミットし直す。

## アニメーション（GIF / WebP）

開いたら自動で再生する。

- GIF：WIC でコマを取り出し、メタデータ（位置・表示時間・Disposal・ループ回数）に従って自前で合成する
  - Disposal 2（背景に戻す）はブラウザに合わせて透明にする
  - ループ回数は Chrome / Firefox と同じ解釈（拡張なし = 1回、0 = 無限、N = N+1回）
- WebP：libwebpdemux（WebPAnimDecoder）が合成済みのコマを返すので、それをそのまま使う
- 表示時間が 10ms 以下のコマは 100ms として扱う（ブラウザと同じ）
- 決められた回数を再生したら最後のコマで止まる（P で最初から再生し直す）
- 処理が遅れたときはコマを飛ばして追いつく。最小化している間は止める
- 全コマを合成済みでメモリに持つ。上限（ini の `AnimationMaxMB`、既定 256MB）を超えるものは1コマ目だけ表示して、ステータスバーに「アニメ省略」と出す
- ズーム・パン・回転は再生中もそのまま効く
- ステータスバーに「コマ 3 / 20」と出す（一時停止中・再生終了も表示）

## 設定の保存

exe と同じフォルダの `ImageViewer.ini` に、終了時に保存して次の起動で読み込む（UTF-8）。

```ini
[Window]
Left=100
Top=80
Width=1280
Height=800
Maximized=false

[Sort]
Key=Name
Descending=false

[Memory]
AnimationMaxMB=256
PrefetchMaxMB=512
```

- `[Window]`：通常時のウィンドウ位置とサイズ（物理ピクセル）と、最大化していたか
  - 最大化・全画面で終了しても、元に戻したときの位置とサイズを保存する。全画面は次回は通常表示で開く（全画面の前に最大化していたら最大化）
  - 保存した位置が画面外（モニタを外した等）なら、OS が画面内に収める
- `[Sort]`：並び順（`Name` / `LastWriteTime` / `Size`）と降順か
- `[Memory]`：メモリの上限（MB、1〜65536）。アプリに設定画面は無いので、ここを手で書き換える
  - `AnimationMaxMB`：アニメーション1枚に使うメモリ。超えたら1コマ目だけ表示
  - `PrefetchMaxMB`：先読みした画像の合計（表示中の画像は含まない）
  - 次の起動から効く。アプリを起動したまま書き換えても、終了時に消されない（この2つはファイル側の値を残す）
- ファイルが無い・値が壊れているときは、その項目だけ既定値を使う。手で書くときは `true/false` の代わりに `1/0` でもいい
- exe の場所に書き込めないと保存されない（起動・終了はふつうにできる）。開発中は `bin\Debug\net10.0-windows\` にできる

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
- 先読みした画像の合計がメモリ上限（ini の `PrefetchMaxMB`、既定 512MB）を超えそうなら、優先度の低いものから諦める（目安。読み込み中の画像は大きさが分からないので、少し超えることはある）
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
| P | アニメーションの一時停止 ⇔ 再生（再生し終わっていたら最初から） |
| , / . | アニメーションのコマ送り（前 / 次。押すと一時停止になる） |

## 今後のTODO

- サムネイル一覧 / EXIF情報の表示 / スライドショー
