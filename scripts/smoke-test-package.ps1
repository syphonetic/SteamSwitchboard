[CmdletBinding()]
param(
    [string]$ArchivePath,

    [string]$ScreenshotPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$projectXml = Get-Content -LiteralPath (
    Join-Path $projectRoot 'src\SteamSwitchboard.App\SteamSwitchboard.App.csproj') -Raw
$expectedVersion = @(
    $projectXml.Project.PropertyGroup |
        ForEach-Object { [string]$_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $projectRoot "artifacts\release\SteamSwitchboard-$expectedVersion-win-x64.zip"
}
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$actualHash = $null
$packageRootName = $null

$resolvedScreenshot = $null
if (-not [string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    $screenshotCandidate = if ([System.IO.Path]::IsPathRooted($ScreenshotPath)) {
        $ScreenshotPath
    }
    else {
        Join-Path $projectRoot $ScreenshotPath
    }
    $resolvedScreenshot = [System.IO.Path]::GetFullPath($screenshotCandidate)
    $projectPrefix = $projectRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $isInsideProject = $resolvedScreenshot.StartsWith(
        $projectPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)
    $isPng = [System.IO.Path]::GetExtension($resolvedScreenshot) -eq '.png'
    if (-not $isInsideProject -or -not $isPng) {
        throw 'The optional screenshot must be a PNG inside the SteamSwitchboard project.'
    }
}

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$extractRoot = Join-Path $temporaryBase "SteamSwitchboard-smoke-$([Guid]::NewGuid().ToString('N'))"
$resolvedExtract = [System.IO.Path]::GetFullPath($extractRoot)
$isSafeTemporaryPath = $resolvedExtract.StartsWith(
    $temporaryBase,
    [System.StringComparison]::OrdinalIgnoreCase)
$hasSafeTemporaryName = ([System.IO.Path]::GetFileName($resolvedExtract)).StartsWith(
    'SteamSwitchboard-smoke-',
    [System.StringComparison]::Ordinal)
if (-not $isSafeTemporaryPath -or -not $hasSafeTemporaryName) {
    throw 'Refusing to use an unexpected smoke-test extraction path.'
}

$localAppData = [System.IO.Path]::GetFullPath(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
$runtimeRoot = [System.IO.Path]::GetFullPath((Join-Path $localAppData 'SteamSwitchboard'))
$expectedRuntimeRoot = Join-Path $localAppData 'SteamSwitchboard'
if (-not $runtimeRoot.Equals(
    $expectedRuntimeRoot,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to use an unexpected application-data path.'
}
if (Test-Path -LiteralPath $runtimeRoot) {
    throw "The smoke test requires clean app data. Move or remove $runtimeRoot first."
}
if (@(Get-Process -Name 'SteamSwitchboard' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'The smoke test requires all existing SteamSwitchboard processes to be closed.'
}

$process = $null
$archiveLock = $null
try {
    $archiveLock = [System.IO.File]::Open(
        $archive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $packageValidation = & (Join-Path $PSScriptRoot 'validate-package.ps1') `
        -ArchivePath $archive `
        -ExpectedVersion $expectedVersion
    $actualHash = $packageValidation.Sha256
    $packageRootName = $packageValidation.PackageRootName

    Expand-Archive -LiteralPath $archive -DestinationPath $resolvedExtract
    $packageRoot = Join-Path $resolvedExtract $packageRootName
    $executable = Join-Path $packageRoot 'SteamSwitchboard.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw 'The packaged executable was not found.'
    }

    $drawingAssembly = if ($PSVersionTable.PSEdition -eq 'Desktop') {
        'System.Drawing'
    }
    else {
        'System.Drawing.Common'
    }
    Add-Type -AssemblyName $drawingAssembly
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class SteamSwitchboardWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
}
'@
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($executable)
    try {
        $iconSize = if ($null -eq $icon) {
            'missing'
        }
        else {
            "$($icon.Width)x$($icon.Height)"
        }
    }
    finally {
        if ($null -ne $icon) {
            $icon.Dispose()
        }
    }
    if ($iconSize -eq 'missing') {
        throw 'The packaged executable does not expose its application icon.'
    }

    $startArguments = @{
        FilePath = $executable
        WorkingDirectory = $packageRoot
        PassThru = $true
    }
    $process = Start-Process @startArguments
    $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while (-not $process.HasExited `
        -and $process.MainWindowHandle -eq [IntPtr]::Zero `
        -and [DateTimeOffset]::UtcNow -lt $startupDeadline)

    if ($process.HasExited) {
        throw "The packaged app exited early with code $($process.ExitCode)."
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The packaged app did not create a main window in time.'
    }
    $expectedWindowTitle = 'SteamSwitchboard {0} unofficial Steam companion' -f [char]0x2014
    if ($process.MainWindowTitle -ne $expectedWindowTitle) {
        throw "The packaged app exposed an unexpected window title: '$($process.MainWindowTitle)'"
    }
    Start-Sleep -Seconds 4
    $process.Refresh()
    if ($process.HasExited) {
        throw "The packaged app exited during startup with code $($process.ExitCode)."
    }

    if ($null -ne $resolvedScreenshot) {
        $windowHandle = $process.MainWindowHandle
        if ($windowHandle -eq [IntPtr]::Zero) {
            throw 'The packaged app did not expose a capturable main window.'
        }

        $rect = [SteamSwitchboardWindowCapture+Rect]::new()
        if (-not [SteamSwitchboardWindowCapture]::GetWindowRect($windowHandle, [ref]$rect)) {
            throw 'Windows did not provide the packaged app window bounds.'
        }

        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        $bitmap = [System.Drawing.Bitmap]::new(
            $width,
            $height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $deviceContext = $graphics.GetHdc()
        try {
            $captured = [SteamSwitchboardWindowCapture]::PrintWindow(
                $windowHandle,
                $deviceContext,
                2)
        }
        finally {
            $graphics.ReleaseHdc($deviceContext)
        }

        try {
            if (-not $captured) {
                $graphics.CopyFromScreen(
                    $rect.Left,
                    $rect.Top,
                    0,
                    0,
                    [System.Drawing.Size]::new($width, $height))
            }
            [System.IO.Directory]::CreateDirectory(
                [System.IO.Path]::GetDirectoryName($resolvedScreenshot)) | Out-Null
            if (Test-Path -LiteralPath $resolvedScreenshot) {
                [System.IO.File]::Delete($resolvedScreenshot)
            }
            $bitmap.Save(
                $resolvedScreenshot,
                [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    [pscustomobject]@{
        Archive = $archive
        ArchiveBytes = (Get-Item -LiteralPath $archive).Length
        Sha256 = $actualHash
        PackageFileCount = @(
            Get-ChildItem -LiteralPath $packageRoot -File -Recurse).Count
        PdbCount = @(
            Get-ChildItem -LiteralPath $packageRoot -Filter '*.pdb' -File -Recurse).Count
        ExecutableVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
        EmbeddedIcon = $iconSize
        ProcessRunning = -not $process.HasExited
        WindowTitle = $process.MainWindowTitle
        RuntimeDataCreated = Test-Path -LiteralPath $runtimeRoot
        StateFileCreated = Test-Path -LiteralPath (Join-Path $runtimeRoot 'state.json')
        BrowserDataRootCreated = Test-Path -LiteralPath (Join-Path $runtimeRoot 'BrowserData')
        Screenshot = $resolvedScreenshot
    }
}
finally {
    if ($null -ne $archiveLock) {
        $archiveLock.Dispose()
    }

    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id -Force
        }
    }

    if (Test-Path -LiteralPath $runtimeRoot) {
        Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $resolvedExtract) {
        Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
    }
}

$cleanupResult = [pscustomobject]@{
    RuntimeDataCleaned = -not (Test-Path -LiteralPath $runtimeRoot)
    ExtractionCleaned = -not (Test-Path -LiteralPath $resolvedExtract)
    AppProcessesRemaining = @(
        Get-Process -Name 'SteamSwitchboard' -ErrorAction SilentlyContinue).Count
}
if (-not $cleanupResult.RuntimeDataCleaned `
    -or -not $cleanupResult.ExtractionCleaned `
    -or $cleanupResult.AppProcessesRemaining -ne 0) {
    throw 'The smoke test did not clean up every process and temporary data folder.'
}
$cleanupResult
