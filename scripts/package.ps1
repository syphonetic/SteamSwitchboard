[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [switch]$RequireSignature,

    [string]$ExpectedPublisher
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'src\SteamSwitchboard.App\SteamSwitchboard.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$version = @(
    $projectXml.Project.PropertyGroup |
        ForEach-Object { [string]$_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'The application version is missing from the project file.'
}

$releaseRoot = Join-Path $projectRoot 'artifacts\release'
$packageName = "SteamSwitchboard-$version-$Runtime"
$archivePath = Join-Path $releaseRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

function New-DeterministicArchive {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$RootName,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $relativePaths = [string[]]@(
        Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse |
            ForEach-Object {
                [System.IO.Path]::GetRelativePath($SourceDirectory, $_.FullName)
            })
    [Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)

    $stream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $archive = [System.IO.Compression.ZipArchive]::new(
        $stream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    $fixedTimestamp = [System.DateTimeOffset]::new(
        2000, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    try {
        foreach ($relativePath in $relativePaths) {
            $entryName = "$RootName/$($relativePath.Replace('\', '/'))"
            $entry = $archive.CreateEntry(
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp
            $entry.ExternalAttributes = 0

            $input = [System.IO.File]::Open(
                (Join-Path $SourceDirectory $relativePath),
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

& (Join-Path $PSScriptRoot 'verify.ps1') -Configuration $Configuration

[System.IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$lockPath = Join-Path $releaseRoot '.package.lock'
$lockStream = $null
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$stagingRoot = Join-Path $temporaryBase "SteamSwitchboard-package-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $stagingRoot $packageName
$resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
$temporaryPrefix = $temporaryBase.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$isUnderTemporaryRoot = $resolvedStaging.StartsWith(
    $temporaryPrefix,
    [System.StringComparison]::OrdinalIgnoreCase)
$hasExpectedStagingName = ([System.IO.Path]::GetFileName($resolvedStaging)).StartsWith(
    'SteamSwitchboard-package-',
    [System.StringComparison]::Ordinal)
if (-not $isUnderTemporaryRoot -or -not $hasExpectedStagingName) {
    throw 'Refusing to use an unexpected staging path.'
}

$temporaryArchive = Join-Path $releaseRoot ".$packageName.$([Guid]::NewGuid().ToString('N')).zip"
$temporaryChecksum = "$temporaryArchive.sha256"

try {
    $lockStream = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)

    & dotnet publish $projectFile `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --property:PublishReadyToRun=true `
        --property:PublishSingleFile=false `
        --property:DebugType=None `
        --property:DebugSymbols=false `
        --property:RestoreLockedMode=true `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish exited with code $LASTEXITCODE."
    }

    foreach ($document in @(
        'README.md',
        'LICENSE',
        'NOTICE.md',
        'SECURITY.md',
        'CHANGELOG.md',
        'CONTRIBUTING.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $publishDirectory
    }

    $packageDocs = Join-Path $publishDirectory 'docs'
    [System.IO.Directory]::CreateDirectory($packageDocs) | Out-Null
    foreach ($document in @(
        'ARCHITECTURE.md',
        'PRIVACY.md',
        'VALIDATION.md',
        'GITHUB_RELEASE.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "docs\$document") -Destination $packageDocs
    }

    $packageArtifacts = Join-Path $publishDirectory 'artifacts'
    [System.IO.Directory]::CreateDirectory($packageArtifacts) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\ui-final.png') -Destination $packageArtifacts

    $packageBranding = Join-Path $publishDirectory (
        'src\SteamSwitchboard.App\Assets\Branding')
    [System.IO.Directory]::CreateDirectory($packageBranding) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot (
        'src\SteamSwitchboard.App\Assets\Branding\SteamSwitchboard-logo-v1.png')) `
        -Destination $packageBranding

    New-DeterministicArchive `
        -SourceDirectory $publishDirectory `
        -RootName $packageName `
        -DestinationPath $temporaryArchive

    $hash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $temporaryChecksum,
        "$hash  $([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))

    $validationArguments = @{
        ArchivePath = $temporaryArchive
        ChecksumPath = $temporaryChecksum
        ExpectedVersion = $version
        RequireSignature = $RequireSignature
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
        $validationArguments.ExpectedPublisher = $ExpectedPublisher
    }
    & (Join-Path $PSScriptRoot 'validate-package.ps1') @validationArguments | Out-Host
    & (Join-Path $PSScriptRoot 'test-package-validator.ps1') `
        -ArchivePath $temporaryArchive `
        -ChecksumPath $temporaryChecksum `
        -ExpectedVersion $version

    [System.IO.File]::Move($temporaryArchive, $archivePath, $true)
    [System.IO.File]::Move($temporaryChecksum, $checksumPath, $true)
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
    if (Test-Path -LiteralPath $lockPath) {
        [System.IO.File]::Delete($lockPath)
    }
    if (Test-Path -LiteralPath $temporaryArchive) {
        [System.IO.File]::Delete($temporaryArchive)
    }
    if (Test-Path -LiteralPath $temporaryChecksum) {
        [System.IO.File]::Delete($temporaryChecksum)
    }
    if (Test-Path -LiteralPath $resolvedStaging) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

Write-Host "Created $archivePath"
Write-Host "Created $checksumPath"
if (-not $RequireSignature) {
    Write-Warning 'This development package is unsigned. Public distribution requires a trusted Authenticode certificate.'
}
