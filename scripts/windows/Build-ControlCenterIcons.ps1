param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Join-Path $repo 'design/phonedeck-console-icon.xaml'
$assets = Join-Path $repo 'work/phone-deck/windows/PhoneDeck.ControlCenter/Assets'
$drawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($source))

function Render-Png([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, [Windows.Rect]::new(0, 0, $size, $size))
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Render-Png $_) })
$iconStream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($iconStream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()
# The same saturated colors work on both themes, including the executable's fixed icon.
foreach ($theme in @('Light', 'Dark')) {
    [IO.File]::WriteAllBytes((Join-Path $assets "PhoneDeck.$theme.ico"), $iconStream.ToArray())
    [IO.File]::WriteAllBytes((Join-Path $assets "PhoneDeck.$theme.png"), (Render-Png 256))
}
$writer.Dispose()
$iconStream.Dispose()
Write-Output 'Generated transparent PNG and multi-resolution ICO assets (16-256 px).'
