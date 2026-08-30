[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SteamSwitchboard.App\Assets\SteamSwitchboard.ico')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName PresentationCore, WindowsBase

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$relativeOutput = [System.IO.Path]::GetRelativePath($expectedRoot, $resolvedOutput)
$escapesProject = [System.IO.Path]::IsPathRooted($relativeOutput) `
    -or $relativeOutput -eq '..' `
    -or $relativeOutput.StartsWith(
        "..$([System.IO.Path]::DirectorySeparatorChar)",
        [System.StringComparison]::Ordinal)
$isIcon = [System.IO.Path]::GetExtension($resolvedOutput).Equals(
    '.ico',
    [System.StringComparison]::OrdinalIgnoreCase)
if ($escapesProject -or -not $isIcon) {
    throw "The icon output must stay inside the SteamSwitchboard project."
}

$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
$currentDirectory = $expectedRoot
$relativeDirectory = [System.IO.Path]::GetRelativePath($expectedRoot, $outputDirectory)
foreach ($component in $relativeDirectory.Split(
    @([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
    [System.StringSplitOptions]::RemoveEmptyEntries)) {
    $currentDirectory = Join-Path $currentDirectory $component
    if (Test-Path -LiteralPath $currentDirectory) {
        $attributes = (Get-Item -LiteralPath $currentDirectory -Force).Attributes
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The icon output path cannot pass through a filesystem link.'
        }
    }
}
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$brandSourcePath = [System.IO.Path]::GetFullPath((Join-Path $expectedRoot (
    'src\SteamSwitchboard.App\Assets\Branding\SteamSwitchboard-app-logo.png')))
if (-not (Test-Path -LiteralPath $brandSourcePath -PathType Leaf)) {
    throw 'The committed SteamSwitchboard brand image is missing.'
}
$brandSource = [System.Windows.Media.Imaging.BitmapImage]::new()
$brandSource.BeginInit()
$brandSource.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$brandSource.UriSource = [Uri]$brandSourcePath
$brandSource.EndInit()
$brandSource.Freeze()

function New-IconPng {
    param([Parameter(Mandatory)][int]$Size)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $background = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromRgb(0x1A, 0x9F, 0xFF))
        $drawing.DrawRoundedRectangle(
            $background,
            $null,
            [System.Windows.Rect]::new(0, 0, $Size, $Size),
            $Size * 0.25,
            $Size * 0.25)

        $stroke = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Colors]::White)
        $pen = [System.Windows.Media.Pen]::new($stroke, [Math]::Max(1.5, $Size * 0.07))
        $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round

        $drawing.DrawLine($pen,
            [System.Windows.Point]::new($Size * 0.23, $Size * 0.35),
            [System.Windows.Point]::new($Size * 0.74, $Size * 0.35))
        $drawing.DrawLine($pen,
            [System.Windows.Point]::new($Size * 0.62, $Size * 0.23),
            [System.Windows.Point]::new($Size * 0.75, $Size * 0.35))
        $drawing.DrawLine($pen,
            [System.Windows.Point]::new($Size * 0.75, $Size * 0.35),
            [System.Windows.Point]::new($Size * 0.62, $Size * 0.47))

        $drawing.DrawLine($pen,
            [System.Windows.Point]::new($Size * 0.77, $Size * 0.65),
            [System.Windows.Point]::new($Size * 0.26, $Size * 0.65))
        $drawing.DrawLine($pen,
            [System.Windows.Point]::new($Size * 0.38, $Size * 0.53),
            [System.Windows.Point]::new($Size * 0.25, $Size * 0.65))
        $drawing.DrawLine($pen,
            [System.Windows.Point]::new($Size * 0.25, $Size * 0.65),
            [System.Windows.Point]::new($Size * 0.38, $Size * 0.77))
    }
    finally {
        $drawing.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function New-BrandIconPng {
    param([Parameter(Mandatory)][int]$Size)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.DrawImage(
            $brandSource,
            [System.Windows.Rect]::new(0, 0, $Size, $Size))
    }
    finally {
        $drawing.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Data = if ($size -le 48) {
            New-IconPng -Size $size
        }
        else {
            New-BrandIconPng -Size $size
        }
    }
}

$temporaryIcon = Join-Path $outputDirectory ".SteamSwitchboard.$([Guid]::NewGuid().ToString('N')).ico.tmp"
$iconWritten = $false
$file = [System.IO.File]::Open(
    $temporaryIcon,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Data.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Data)
    }
    $iconWritten = $true
}
finally {
    $writer.Dispose()
    $file.Dispose()
    if (-not $iconWritten -and (Test-Path -LiteralPath $temporaryIcon)) {
        [System.IO.File]::Delete($temporaryIcon)
    }
}

try {
    [System.IO.File]::Move($temporaryIcon, $resolvedOutput, $true)
}
finally {
    if (Test-Path -LiteralPath $temporaryIcon) {
        [System.IO.File]::Delete($temporaryIcon)
    }
}

Write-Host "Generated $($images.Count)-size icon at $resolvedOutput"
