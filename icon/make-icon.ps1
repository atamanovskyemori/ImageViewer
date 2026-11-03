# icon.svg からアプリのアイコン（src\ImageViewer\ImageViewer.ico）を作る。
# Windows PowerShell 5.1 で動かす（WPF で描くので STA が要る。5.1 は既定で STA）：
#   powershell -ExecutionPolicy Bypass -File icon\make-icon.ps1
# 16px（$SmallMax 以下）は、icon-small.svg があればそちらを使う（線を太くするなど、小さく出したときの見え方を整えた絵）。
# -PreviewDir を付けると、各サイズの PNG もそこに書き出す（見え方の確認用）。
#
# 読める SVG はこのアイコンに使っている分だけ：rect（rx）・circle・polygon・path（WPF と同じ書き方の d）・g（clip-path）、
# 色は fill（#RRGGBB）。それ以外が出てきたら止める。
param(
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$Sizes = 16, 20, 24, 32, 40, 48, 64, 256
$SmallMax = 16
$Output = Join-Path $PSScriptRoot '..\src\ImageViewer\ImageViewer.ico'

function Num([string]$value, [double]$default = 0) {
    if ([string]::IsNullOrEmpty($value)) { return $default }
    return [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Geometry([Xml.XmlElement]$el) {
    switch ($el.LocalName) {
        'rect' {
            $rect = New-Object Windows.Rect (Num $el.GetAttribute('x')), (Num $el.GetAttribute('y')), (Num $el.GetAttribute('width')), (Num $el.GetAttribute('height'))
            $r = Num $el.GetAttribute('rx')
            return New-Object Windows.Media.RectangleGeometry $rect, $r, $r
        }
        'circle' {
            $center = New-Object Windows.Point (Num $el.GetAttribute('cx')), (Num $el.GetAttribute('cy'))
            $r = Num $el.GetAttribute('r')
            return New-Object Windows.Media.EllipseGeometry $center, $r, $r
        }
        'polygon' {
            $points = $el.GetAttribute('points').Trim() -split '\s+'
            return [Windows.Media.Geometry]::Parse('M ' + ($points -join ' L ') + ' Z')
        }
        'path' {
            return [Windows.Media.Geometry]::Parse($el.GetAttribute('d'))
        }
        default { throw "読めない要素です: <$($el.LocalName)>" }
    }
}

function Get-Clip([Xml.XmlElement]$el, [Xml.XmlElement]$svg) {
    $ref = $el.GetAttribute('clip-path')
    if (-not $ref) { return $null }
    if ($ref -notmatch '^url\(#(.+)\)$') { throw "読めない clip-path です: $ref" }
    $clip = $svg.SelectSingleNode("//*[local-name()='clipPath'][@id='$($Matches[1])']")
    if (-not $clip) { throw "clipPath が見つかりません: $ref" }
    return Get-Geometry ($clip.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] } | Select-Object -First 1)
}

function Draw-Children($dc, [Xml.XmlElement]$parent, [Xml.XmlElement]$svg) {
    foreach ($el in $parent.ChildNodes) {
        if ($el -isnot [Xml.XmlElement] -or $el.LocalName -eq 'defs') { continue }

        $clip = Get-Clip $el $svg
        if ($clip) { $dc.PushClip($clip) }

        if ($el.LocalName -eq 'g') {
            Draw-Children $dc $el $svg
        }
        else {
            $fill = $el.GetAttribute('fill')
            if ($fill -notmatch '^#[0-9A-Fa-f]{6}$') { throw "読めない fill です: <$($el.LocalName) fill=`"$fill`">" }
            $brush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($fill))
            $dc.DrawGeometry($brush, $null, (Get-Geometry $el))
        }

        if ($clip) { $dc.Pop() }
    }
}

function Render-Png([string]$svgPath, [int]$size) {
    $doc = New-Object Xml.XmlDocument
    $doc.Load($svgPath)
    $svg = $doc.DocumentElement
    $viewBox = ($svg.GetAttribute('viewBox').Trim() -split '[\s,]+') | ForEach-Object { Num $_ }

    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($size / $viewBox[2]), ($size / $viewBox[3])))
    $dc.PushTransform((New-Object Windows.Media.TranslateTransform (-$viewBox[0]), (-$viewBox[1])))
    Draw-Children $dc $svg $svg
    $dc.Pop(); $dc.Pop()
    $dc.Close()

    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

$large = Join-Path $PSScriptRoot 'icon.svg'
$small = Join-Path $PSScriptRoot 'icon-small.svg'
if (-not (Test-Path $small)) { $small = $large }

$images = foreach ($size in $Sizes) {
    $png = Render-Png $(if ($size -le $SmallMax) { $small } else { $large }) $size
    if ($PreviewDir) {
        New-Item -ItemType Directory -Force $PreviewDir | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $PreviewDir "icon-$size.png"), $png)
    }
    [pscustomobject]@{ Size = $size; Png = $png }
}

# ICO：ヘッダ（6バイト）、各サイズの目次（16バイトずつ）、PNG をそのまま並べる（Windows Vista 以降は PNG のままで読める）
$out = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $side = if ($image.Size -ge 256) { 0 } else { $image.Size } # 256 は 0 と書く決まり
    $writer.Write([byte]$side); $writer.Write([byte]$side)
    $writer.Write([byte]0); $writer.Write([byte]0)       # 色数・予約
    $writer.Write([uint16]1); $writer.Write([uint16]32)  # プレーン数・ビット数
    $writer.Write([uint32]$image.Png.Length); $writer.Write([uint32]$offset)
    $offset += $image.Png.Length
}
foreach ($image in $images) { $writer.Write($image.Png) }
$writer.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Output), $out.ToArray())
"wrote $([IO.Path]::GetFullPath($Output)) ($($images.Count) sizes, $($out.Length) bytes)"
