[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ChecksumPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSourceRevision,
    [Parameter(Mandatory)][string]$StagingRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'path-utils.ps1')
. (Join-Path $PSScriptRoot 'signing-staging-utils.ps1')

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedStaging = Get-ValidatedSigningStagingRoot -StagingRoot $StagingRoot
if (Test-Path -LiteralPath $resolvedStaging) {
    throw 'The signing-staging path must not already exist.'
}

$sourceRevisionOutput = @(& git -C $projectRoot rev-parse --verify HEAD)
if ($LASTEXITCODE -ne 0 `
    -or $sourceRevisionOutput.Count -ne 1 `
    -or -not $sourceRevisionOutput[0].Trim().Equals(
        $ExpectedSourceRevision,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The signing candidate does not match the current Git revision.'
}
$worktreeChanges = @(& git -C $projectRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $worktreeChanges.Count -ne 0) {
    throw 'Signing preparation requires a clean Git worktree.'
}

$validation = & (Join-Path $PSScriptRoot 'validate-package.ps1') `
    -ArchivePath $ArchivePath `
    -ChecksumPath $ChecksumPath `
    -ExpectedVersion $ExpectedVersion `
    -ExpectedSourceRevision $ExpectedSourceRevision
if ($validation.SignatureStatus -ne 'NotSigned') {
    throw 'The reproducible signing candidate must begin unsigned.'
}

$payload = Join-Path $resolvedStaging 'payload'
$expanded = Join-Path $resolvedStaging 'expanded'
$succeeded = $false
try {
    [System.IO.Directory]::CreateDirectory($expanded) | Out-Null
    Expand-Archive -LiteralPath $validation.Archive -DestinationPath $expanded
    $postExtractionArchiveHash = (
        Get-FileHash -LiteralPath $validation.Archive -Algorithm SHA256).Hash
    if (-not $postExtractionArchiveHash.Equals(
            $validation.Sha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The signing candidate changed while it was extracted.'
    }
    $expandedPackage = Join-Path $expanded $validation.PackageRootName
    if (-not (Test-Path -LiteralPath $expandedPackage -PathType Container)) {
        throw 'The validated package root was not extracted.'
    }
    Move-Item -LiteralPath $expandedPackage -Destination $payload
    Remove-Item -LiteralPath $expanded -Recurse -Force

    $payloadItems = @(Get-Item -LiteralPath $payload) + @(
        Get-ChildItem -LiteralPath $payload -Force -Recurse)
    foreach ($item in $payloadItems) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The extracted signing payload contains a filesystem link.'
        }
    }

    $relativeFiles = [string[]]@(
        Get-ChildItem -LiteralPath $payload -File -Force -Recurse |
            ForEach-Object {
                (Get-ContainedRelativePath `
                    -RootPath $payload `
                    -CandidatePath $_.FullName).Replace('\', '/')
            })
    [Array]::Sort($relativeFiles, [System.StringComparer]::Ordinal)
    if ($relativeFiles.Count -lt 2 -or $relativeFiles.Count -gt 4096) {
        throw 'The extracted signing payload has an unsafe file count.'
    }

    $fileEntries = @(
        foreach ($relativePath in $relativeFiles) {
            if (-not (Test-SafeSigningRelativePath -Path $relativePath)) {
                throw 'The extracted signing payload has an unsafe relative path.'
            }
            $file = Get-Item -LiteralPath (Join-Path $payload $relativePath)
            $authenticodeContentSha256 = if (@(
                    'SteamSwitchboard.dll',
                    'SteamSwitchboard.exe') -ccontains $relativePath) {
                Get-PeAuthenticodeContentSha256 `
                    -Path $file.FullName `
                    -UnsignedLength $file.Length `
                    -RequireUnsigned
            }
            else {
                $null
            }
            [ordered]@{
                path = $relativePath
                length = [long]$file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                authenticodeContentSha256 = $authenticodeContentSha256
            }
        })

    $manifest = [ordered]@{
        schemaVersion = 1
        packageName = $validation.PackageRootName
        version = $ExpectedVersion
        runtime = 'win-x64'
        sourceRevision = $ExpectedSourceRevision.ToLowerInvariant()
        sourceArchiveSha256 = $validation.Sha256.ToLowerInvariant()
        payloadDirectory = 'payload'
        signablePaths = @('SteamSwitchboard.dll', 'SteamSwitchboard.exe')
        files = $fileEntries
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText(
        (Join-Path $resolvedStaging 'signing-manifest.json'),
        "$manifestJson$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))

    $prepared = Assert-SigningStagingIntegrity `
        -StagingRoot $resolvedStaging `
        -ExpectedVersion $ExpectedVersion `
        -ExpectedSourceRevision $ExpectedSourceRevision `
        -ExpectedUnsignedArchiveSha256 $validation.Sha256 `
        -Phase Prepared

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
        throw 'The source changed while the signing candidate was prepared.'
    }

    $succeeded = $true
    [pscustomobject]@{
        StagingRoot = $resolvedStaging
        Payload = $prepared.Context.Payload
        PackageName = $prepared.Context.PackageName
        Version = $prepared.Context.Version
        SourceRevision = $prepared.Context.SourceRevision
        UnsignedArchiveSha256 = $prepared.Context.SourceArchiveSha256
        FileCount = $prepared.FileCount
    }
}
finally {
    if (-not $succeeded -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
