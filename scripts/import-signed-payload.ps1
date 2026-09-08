[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingRoot,
    [Parameter(Mandatory)][string]$SignedPayloadRoot,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSourceRevision,
    [Parameter(Mandatory)][string]$ExpectedUnsignedArchiveSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'signing-staging-utils.ps1')

$prepared = Assert-SigningStagingIntegrity `
    -StagingRoot $StagingRoot `
    -ExpectedVersion $ExpectedVersion `
    -ExpectedSourceRevision $ExpectedSourceRevision `
    -ExpectedUnsignedArchiveSha256 $ExpectedUnsignedArchiveSha256 `
    -Phase Prepared

$signedPayload = [System.IO.Path]::GetFullPath($SignedPayloadRoot)
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryPrefix = $temporaryBase.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $signedPayload.StartsWith(
        $temporaryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase) `
    -or -not [System.IO.Path]::GetFileName($signedPayload).StartsWith(
        'SteamSwitchboard-signed-payload-',
        [System.StringComparison]::Ordinal) `
    -or -not (Test-Path -LiteralPath $signedPayload -PathType Container)) {
    throw 'Refusing to import an unexpected signed-payload path.'
}

$signedItems = @(Get-Item -LiteralPath $signedPayload) + @(
    Get-ChildItem -LiteralPath $signedPayload -Force -Recurse)
foreach ($item in $signedItems) {
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The downloaded signed payload contains a filesystem link.'
    }
}
$signedFiles = @(Get-ChildItem -LiteralPath $signedPayload -File -Force -Recurse)
if ($signedFiles.Count -ne $prepared.FileCount `
    -or $signedFiles.Count -lt 2 `
    -or $signedFiles.Count -gt 4096) {
    throw 'The downloaded signed payload has an unexpected file count.'
}

$destination = $prepared.Context.Payload
if ([System.IO.Directory]::Exists($destination)) {
    [System.IO.Directory]::Delete($destination, $true)
}
[System.IO.Directory]::Move($signedPayload, $destination)

[pscustomobject]@{
    StagingRoot = $prepared.Context.Root
    Payload = $destination
    ImportedFileCount = $signedFiles.Count
}
