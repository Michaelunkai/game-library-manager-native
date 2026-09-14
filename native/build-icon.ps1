param(
    [Parameter(Mandatory = $true)][string]$SourcePng,
    [string]$MasterPng = (Join-Path $PSScriptRoot 'Assets\GameLibrary-master.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot 'Assets\GameLibrary.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore

$source = [IO.Path]::GetFullPath($SourcePng)
$master = [IO.Path]::GetFullPath($MasterPng)
$output = [IO.Path]::GetFullPath($OutputIco)
if (-not [IO.File]::Exists($source)) { throw "Icon source does not exist: $source" }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($master)) | Out-Null
[IO.File]::Copy($source, $master, $true)

$bitmap = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]$master)
if ($bitmap.PixelWidth -ne $bitmap.PixelHeight -or $bitmap.PixelWidth -lt 256) {
    throw "Icon master must be square and at least 256 pixels: $($bitmap.PixelWidth)x$($bitmap.PixelHeight)"
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $scale = $size / [double]$bitmap.PixelWidth
    $resized = [Windows.Media.Imaging.TransformedBitmap]::new($bitmap, [Windows.Media.ScaleTransform]::new($scale, $scale))
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($resized))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    ,$stream.ToArray()
}

$file = [IO.File]::Open($output, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
}
finally { $writer.Dispose(); $file.Dispose() }

[pscustomobject]@{
    Master = $master
    Icon = $output
    Frames = $sizes -join ', '
    Sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
}
