[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath,

    [Parameter(Mandatory)]
    [string]$ChecksumPath,

    [Parameter(Mandatory)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'path-utils.ps1')

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$checksum = (Resolve-Path -LiteralPath $ChecksumPath).Path
$validator = Join-Path $PSScriptRoot 'validate-package.ps1'
$baseline = & $validator `
    -ArchivePath $archive `
    -ChecksumPath $checksum `
    -ExpectedVersion $ExpectedVersion
$packageRootName = $baseline.PackageRootName

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryBase "SteamSwitchboard-validator-tests-$([Guid]::NewGuid().ToString('N'))"
$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
$temporaryPrefix = $temporaryBase.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedTestRoot.StartsWith(
        $temporaryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase) `
    -or -not ([System.IO.Path]::GetFileName($resolvedTestRoot)).StartsWith(
        'SteamSwitchboard-validator-tests-',
        [System.StringComparison]::Ordinal)) {
    throw 'Refusing to use an unexpected package-validator test path.'
}
[System.IO.Directory]::CreateDirectory($resolvedTestRoot) | Out-Null

function Write-TestChecksum {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $sidecar = "$Path.sha256"
    [System.IO.File]::WriteAllText(
        $sidecar,
        "$hash  $packageRootName.zip$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))
    return $sidecar
}

function Assert-ValidatorRejects {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Sidecar,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$ExpectedMessagePattern
    )

    $rejectionMessage = $null
    try {
        & $validator `
            -ArchivePath $Path `
            -ChecksumPath $Sidecar `
            -ExpectedVersion $ExpectedVersion | Out-Null
    }
    catch {
        $rejectionMessage = $_.Exception.Message
    }
    if ([string]::IsNullOrWhiteSpace($rejectionMessage)) {
        throw "The package validator accepted a malicious $Scenario fixture."
    }
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
            $rejectionMessage,
            $ExpectedMessagePattern,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        throw "The package validator rejected the $Scenario fixture for the wrong reason: $rejectionMessage"
    }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
try {
    foreach ($validVersion in @(
            '1.0.0',
            '1.0.0+ABCDEF012345',
            '1.0.0+build.42')) {
        if (-not (Test-ReleaseProductVersion `
                -ProductVersion $validVersion `
                -ExpectedVersion '1.0.0')) {
            throw "The release-version validator rejected '$validVersion'."
        }
    }
    if (-not (Test-ReleaseProductVersion `
            -ProductVersion '1.0.0-rc.1+build.42' `
            -ExpectedVersion '1.0.0-rc.1')) {
        throw 'The release-version validator rejected prerelease build metadata.'
    }
    foreach ($invalidVersion in @(
            '1.0.01',
            '1.0.0-malformed',
            '1.0.0+',
            '1.0.0+build..42',
            "1.0.0+build$([Environment]::NewLine)",
            ("1.0.0+" + ('a' * 129)))) {
        if (Test-ReleaseProductVersion `
                -ProductVersion $invalidVersion `
                -ExpectedVersion '1.0.0') {
            throw "The release-version validator accepted '$invalidVersion'."
        }
    }

    $misnamedChecksum = Join-Path $resolvedTestRoot 'misnamed.sha256'
    $baselineHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $misnamedChecksum,
        "$baselineHash  wrong-package.zip$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))
    Assert-ValidatorRejects `
        -Path $archive `
        -Sidecar $misnamedChecksum `
        -Scenario 'misnamed-checksum' `
        -ExpectedMessagePattern 'checksum file is malformed'

    $traversalArchive = Join-Path $resolvedTestRoot 'traversal.zip'
    $traversalStream = [System.IO.File]::Open(
        $traversalArchive,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $traversalZip = [System.IO.Compression.ZipArchive]::new(
        $traversalStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        $entry = $traversalZip.CreateEntry("$packageRootName/../escape.txt")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('escape')
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $traversalZip.Dispose()
        $traversalStream.Dispose()
    }
    $traversalChecksum = Write-TestChecksum -Path $traversalArchive
    Assert-ValidatorRejects `
        -Path $traversalArchive `
        -Sidecar $traversalChecksum `
        -Scenario 'path traversal' `
        -ExpectedMessagePattern 'unsafe path|unexpected root'

    $debugArchive = Join-Path $resolvedTestRoot 'debug-data.zip'
    Copy-Item -LiteralPath $archive -Destination $debugArchive
    $debugStream = [System.IO.File]::Open(
        $debugArchive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $debugZip = [System.IO.Compression.ZipArchive]::new(
        $debugStream,
        [System.IO.Compression.ZipArchiveMode]::Update,
        $false)
    try {
        $entry = $debugZip.CreateEntry("$packageRootName/leaked.pdb")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('debug data')
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $debugZip.Dispose()
        $debugStream.Dispose()
    }
    $debugChecksum = Write-TestChecksum -Path $debugArchive
    Assert-ValidatorRejects `
        -Path $debugArchive `
        -Sidecar $debugChecksum `
        -Scenario 'debug-data' `
        -ExpectedMessagePattern 'debug output|local session data'

    $duplicateArchive = Join-Path $resolvedTestRoot 'duplicate.zip'
    Copy-Item -LiteralPath $archive -Destination $duplicateArchive
    $duplicateStream = [System.IO.File]::Open(
        $duplicateArchive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $duplicateZip = [System.IO.Compression.ZipArchive]::new(
        $duplicateStream,
        [System.IO.Compression.ZipArchiveMode]::Update,
        $false)
    try {
        $entry = $duplicateZip.CreateEntry("$packageRootName/readme.MD")
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('duplicate')
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $duplicateZip.Dispose()
        $duplicateStream.Dispose()
    }
    $duplicateChecksum = Write-TestChecksum -Path $duplicateArchive
    Assert-ValidatorRejects `
        -Path $duplicateArchive `
        -Sidecar $duplicateChecksum `
        -Scenario 'case-colliding path' `
        -ExpectedMessagePattern 'unsafe or duplicate entry name'

    $alteredLogoArchive = Join-Path $resolvedTestRoot 'altered-notification-logo.zip'
    Copy-Item -LiteralPath $archive -Destination $alteredLogoArchive
    $alteredLogoStream = [System.IO.File]::Open(
        $alteredLogoArchive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $alteredLogoZip = [System.IO.Compression.ZipArchive]::new(
        $alteredLogoStream,
        [System.IO.Compression.ZipArchiveMode]::Update,
        $false)
    try {
        $logoEntryName = "$packageRootName/Assets/Branding/SteamSwitchboard-app-logo.png"
        $logoEntry = $alteredLogoZip.GetEntry($logoEntryName)
        if ($null -eq $logoEntry) {
            throw 'The baseline package has no notification logo fixture.'
        }
        $logoEntry.Delete()
        $replacement = $alteredLogoZip.CreateEntry($logoEntryName)
        $writer = [System.IO.StreamWriter]::new($replacement.Open())
        try {
            $writer.Write('unreviewed replacement artwork')
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $alteredLogoZip.Dispose()
        $alteredLogoStream.Dispose()
    }
    $alteredLogoChecksum = Write-TestChecksum -Path $alteredLogoArchive
    Assert-ValidatorRejects `
        -Path $alteredLogoArchive `
        -Sidecar $alteredLogoChecksum `
        -Scenario 'altered notification logo' `
        -ExpectedMessagePattern 'does not match the reviewed SteamSwitchboard artwork'

    $compressionArchive = Join-Path $resolvedTestRoot 'compression-bomb.zip'
    Copy-Item -LiteralPath $archive -Destination $compressionArchive
    $compressionStream = [System.IO.File]::Open(
        $compressionArchive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $compressionZip = [System.IO.Compression.ZipArchive]::new(
        $compressionStream,
        [System.IO.Compression.ZipArchiveMode]::Update,
        $false)
    try {
        $entry = $compressionZip.CreateEntry(
            "$packageRootName/compression-fixture.bin",
            [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        try {
            $zeroes = [byte[]]::new(1MB)
            foreach ($block in 1..16) {
                $entryStream.Write($zeroes, 0, $zeroes.Length)
            }
        }
        finally {
            $entryStream.Dispose()
        }
    }
    finally {
        $compressionZip.Dispose()
        $compressionStream.Dispose()
    }
    $compressionChecksum = Write-TestChecksum -Path $compressionArchive
    Assert-ValidatorRejects `
        -Path $compressionArchive `
        -Sidecar $compressionChecksum `
        -Scenario 'resource-amplification' `
        -ExpectedMessagePattern 'suspiciously compressed entry'

    if ($baseline.SignatureStatus -ne 'Valid') {
        $signatureRejectionMessage = $null
        try {
            & $validator `
                -ArchivePath $archive `
                -ChecksumPath $checksum `
                -ExpectedVersion $ExpectedVersion `
                -RequireSignature | Out-Null
        }
        catch {
            $signatureRejectionMessage = $_.Exception.Message
        }
        if ([string]::IsNullOrWhiteSpace($signatureRejectionMessage)) {
            throw 'The package validator accepted an unsigned package when a signature was required.'
        }
        if ($signatureRejectionMessage -notmatch 'signature') {
            throw "Signature enforcement failed for the wrong reason: $signatureRejectionMessage"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

if (Test-Path -LiteralPath $resolvedTestRoot) {
    throw 'Package-validator tests could not clean their temporary files.'
}

Write-Host 'Package-validator adversarial tests passed.'
