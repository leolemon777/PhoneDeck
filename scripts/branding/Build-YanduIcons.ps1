param([string]$PreviewDirectory)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'The optional icon renderer uses Windows WPF. Android builds use the committed XML/PNG assets on every OS.'
}
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resourceRoot = Join-Path $repo 'work/phone-deck/android/app/src/main/res'
$artwork = Join-Path $repo 'work/phone-deck/android/artwork'
if (-not $PreviewDirectory) { $PreviewDirectory = Join-Path $repo 'outputs/yandu-brand' }
New-Item -ItemType Directory -Path $artwork,$PreviewDirectory -Force | Out-Null
[xml]$source = [IO.File]::ReadAllText((Join-Path $repo 'design/yandu-icon.svg'))
$background = $source.svg.rect.fill
$foreground = $source.svg.path.fill
$pathData = $source.svg.path.d
$geometry = [Windows.Media.Geometry]::Parse($pathData).Clone()
$geometry.FillRule = [Windows.Media.FillRule]::EvenOdd
$geometry.Freeze()
$backgroundBrush = [Windows.Media.BrushConverter]::new().ConvertFromString($background)
$foregroundBrush = [Windows.Media.BrushConverter]::new().ConvertFromString($foreground)

function Render-Png([int]$Size, [string]$Mask = 'square') {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($Size / 108.0, $Size / 108.0))
    $clip = switch ($Mask) {
        'circle' { [Windows.Media.EllipseGeometry]::new([Windows.Point]::new(54,54),54,54) }
        'rounded' { [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(0,0,108,108),24,24) }
        default { [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(0,0,108,108)) }
    }
    $drawing.PushClip($clip)
    $drawing.DrawRectangle($backgroundBrush,$null,[Windows.Rect]::new(0,0,108,108))
    $drawing.DrawGeometry($foregroundBrush,$null,$geometry)
    $drawing.Pop(); $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}

function Write-Resource([string]$RelativePath,[string]$Content) {
    $target = Join-Path $resourceRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($target,$Content + "`n",[Text.UTF8Encoding]::new($false))
}

$vectorOpen = '<vector xmlns:android="http://schemas.android.com/apk/res/android" android:width="108dp" android:height="108dp" android:viewportWidth="108" android:viewportHeight="108">'
$mark = "    <path android:fillColor=`"$foreground`" android:fillType=`"evenOdd`" android:pathData=`"$pathData`" />"
Write-Resource 'drawable/ic_launcher_foreground.xml' ($vectorOpen + "`n" + $mark + "`n</vector>")
Write-Resource 'drawable/ic_launcher_monochrome.xml' ($vectorOpen + "`n" + $mark.Replace($foreground,'#FFFFFFFF') + "`n</vector>")
Write-Resource 'drawable/ic_launcher.xml' ($vectorOpen + "`n    <path android:fillColor=`"$background`" android:pathData=`"M0 0H108V108H0Z`" />`n" + $mark + "`n</vector>")
Write-Resource 'values/launcher_colors.xml' "<resources>`n    <color name=`"launcher_background`">$background</color>`n</resources>"
$adaptive = @'
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/launcher_background" />
    <foreground android:drawable="@drawable/ic_launcher_foreground" />
</adaptive-icon>
'@
$themed = $adaptive.Replace('</adaptive-icon>',"    <monochrome android:drawable=`"@drawable/ic_launcher_monochrome`" />`n</adaptive-icon>")
foreach ($qualifier in @('mipmap-anydpi-v26','mipmap-night-anydpi-v26')) {
    Write-Resource "$qualifier/ic_launcher.xml" $adaptive
}
foreach ($qualifier in @('mipmap-anydpi-v33','mipmap-night-anydpi-v33')) {
    Write-Resource "$qualifier/ic_launcher.xml" $themed
}
foreach ($density in @{mdpi=48;hdpi=72;xhdpi=96;xxhdpi=144;xxxhdpi=192}.GetEnumerator()) {
    $bytes = Render-Png $density.Value
    foreach ($prefix in @('mipmap-','mipmap-night-')) {
        $directory = Join-Path $resourceRoot ($prefix + $density.Key)
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $directory 'ic_launcher.png'),$bytes)
    }
}
[IO.File]::WriteAllBytes((Join-Path $artwork 'yandu-app-icon-1024.png'),(Render-Png 1024))
foreach ($mask in @('square','rounded','circle')) {
    [IO.File]::WriteAllBytes((Join-Path $PreviewDirectory "yandu-icon-$mask.png"),(Render-Png 512 $mask))
}
Write-Output "Rendered Yandu vectors, five Android densities, day/night adaptive icons, API 33 monochrome and three mask previews in $PreviewDirectory."
