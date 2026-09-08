[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingRoot,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ChecksumPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSourceRevision,
    [Parameter(Mandatory)][string]$ExpectedUnsignedArchiveSha256,
    [Parameter(Mandatory)][string]$ExpectedPublisher
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'path-utils.ps1')
. (Join-Path $PSScriptRoot 'signing-staging-utils.ps1')

if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) `
    -or $ExpectedPublisher.Length -gt 256 `
    -or $ExpectedPublisher -match '[\x00-\x1F\x7F]') {
    throw 'The expected signing publisher is invalid.'
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedStaging = Get-ValidatedSigningStagingRoot `
    -StagingRoot $StagingRoot `
    -RequireExisting
$signed = Assert-SigningStagingIntegrity `
    -StagingRoot $resolvedStaging `
    -ExpectedVersion $ExpectedVersion `
    -ExpectedSourceRevision $ExpectedSourceRevision `
    -ExpectedUnsignedArchiveSha256 $ExpectedUnsignedArchiveSha256 `
    -Phase Signed `
    -ExpectedPublisher $ExpectedPublisher

$archive = [System.IO.Path]::GetFullPath($ArchivePath)
$checksum = [System.IO.Path]::GetFullPath($ChecksumPath)
$expectedArchiveName = "$($signed.Context.PackageName).zip"
if ([System.IO.Path]::GetFileName($archive) -cne $expectedArchiveName `
    -or [System.IO.Path]::GetFileName($checksum) -cne "$expectedArchiveName.sha256" `
    -or -not [System.IO.Path]::GetDirectoryName($archive).Equals(
        [System.IO.Path]::GetDirectoryName($checksum),
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The signed release output paths do not match the package identity.'
}
if (-not (Test-Path -LiteralPath $archive -PathType Leaf) `
    -or -not (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.Equals(
        $ExpectedUnsignedArchiveSha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The unsigned reproducible archive changed before signed finalization.'
}

$sourceRevisionOutput = @(& git -C $projectRoot rev-parse --verify HEAD)
if ($LASTEXITCODE -ne 0 `
    -or $sourceRevisionOutput.Count -ne 1 `
    -or -not $sourceRevisionOutput[0].Trim().Equals(
        $ExpectedSourceRevision,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Signed finalization does not match the current Git revision.'
}
$worktreeChanges = @(& git -C $projectRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $worktreeChanges.Count -ne 0) {
    throw 'Signed finalization requires a clean Git worktree.'
}

$outputDirectory = [System.IO.Path]::GetDirectoryName($archive)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$lockPath = Join-Path $outputDirectory '.package.lock'
$temporaryArchive = Join-Path $outputDirectory (
    ".$expectedArchiveName.$([Guid]::NewGuid().ToString('N')).tmp")
$temporaryChecksum = "$temporaryArchive.sha256"
$lockStream = $null
$published = $false
try {
    $lockStream = [System.IO.File]::Open(
        $lockPath,
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)

    New-DeterministicArchive `
        -SourceDirectory $signed.Context.Payload `
        -RootName $signed.Context.PackageName `
        -DestinationPath $temporaryArchive
    $signedHash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $temporaryChecksum,
        "$signedHash  $expectedArchiveName$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))

    $validation = & (Join-Path $PSScriptRoot 'validate-package.ps1') `
        -ArchivePath $temporaryArchive `
        -ChecksumPath $temporaryChecksum `
        -ExpectedVersion $ExpectedVersion `
        -ExpectedSourceRevision $ExpectedSourceRevision `
        -ExpectedPublisher $ExpectedPublisher `
        -RequireSignature
    & (Join-Path $PSScriptRoot 'test-package-validator.ps1') `
        -ArchivePath $temporaryArchive `
        -ChecksumPath $temporaryChecksum `
        -ExpectedVersion $ExpectedVersion `
        -ExpectedSourceRevision $ExpectedSourceRevision

    $finalSourceRevisionOutput = @(& git -C $projectRoot rev-parse --verify HEAD)
    $sourceStatus = $LASTEXITCODE
    $finalWorktreeChanges = @(
        & git -C $projectRoot status --porcelain=v1 --untracked-files=all)
    $worktreeStatus = $LASTEXITCODE
    if ($sourceStatus -ne 0 `
        -or $worktreeStatus -ne 0 `
        -or $finalSourceRevisionOutput.Count -ne 1 `
        -or -not $finalSourceRevisionOutput[0].Trim().Equals(
            $ExpectedSourceRevision,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or $finalWorktreeChanges.Count -ne 0) {
        throw 'The source changed while the signed release was finalized.'
    }

    Move-FileReplacing -SourcePath $temporaryArchive -DestinationPath $archive
    Move-FileReplacing -SourcePath $temporaryChecksum -DestinationPath $checksum
    $published = $true

    [pscustomobject]@{
        Archive = $archive
        Checksum = $checksum
        Sha256 = $validation.Sha256
        PackageName = $validation.PackageRootName
        Version = $ExpectedVersion
        SourceRevision = $validation.SourceRevision
        Publisher = $ExpectedPublisher
        SignerThumbprint = $signed.SignerThumbprint
        Timestamped = $true
    }
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

if (-not $published) {
    throw 'The signed release package was not published.'
}
