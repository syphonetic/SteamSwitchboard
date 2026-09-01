[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingRoot,
    [Parameter(Mandatory)][string]$SignedBinariesRoot,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSourceRevision,
    [Parameter(Mandatory)][string]$ExpectedUnsignedArchiveSha256,
    [Parameter(Mandatory)][string]$ExpectedPublisher
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'signing-staging-utils.ps1')

if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) `
    -or $ExpectedPublisher.Length -gt 256 `
    -or $ExpectedPublisher -match '[\x00-\x1F\x7F]') {
    throw 'The expected signing publisher is invalid.'
}

$prepared = Assert-SigningStagingIntegrity `
    -StagingRoot $StagingRoot `
    -ExpectedVersion $ExpectedVersion `
    -ExpectedSourceRevision $ExpectedSourceRevision `
    -ExpectedUnsignedArchiveSha256 $ExpectedUnsignedArchiveSha256 `
    -Phase Prepared

$signedRoot = [System.IO.Path]::GetFullPath($SignedBinariesRoot)
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryPrefix = $temporaryBase.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$comparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
    [System.StringComparison]::OrdinalIgnoreCase
}
else {
    [System.StringComparison]::Ordinal
}

if (-not $signedRoot.StartsWith($temporaryPrefix, $comparison) `
    -or -not [System.IO.Path]::GetFileName($signedRoot).StartsWith(
        'SteamSwitchboard-signpath-response-',
        [System.StringComparison]::Ordinal) `
    -or -not (Test-Path -LiteralPath $signedRoot -PathType Container)) {
    throw 'Refusing to import an unexpected SignPath-response path.'
}

$signedItems = @(Get-Item -LiteralPath $signedRoot) + @(
    Get-ChildItem -LiteralPath $signedRoot -Force)
foreach ($item in $signedItems) {
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The SignPath response contains a filesystem link.'
    }
}
if (@(Get-ChildItem -LiteralPath $signedRoot -Directory -Force).Count -ne 0) {
    throw 'The SignPath response contains an unexpected directory.'
}

$expectedNames = [string[]]@('SteamSwitchboard.dll', 'SteamSwitchboard.exe')
$signedFiles = @(Get-ChildItem -LiteralPath $signedRoot -File -Force)
if ($signedFiles.Count -ne $expectedNames.Count) {
    throw 'The SignPath response does not contain exactly two first-party binaries.'
}
$actualNames = [string[]]@($signedFiles | ForEach-Object { $_.Name })
[Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
for ($index = 0; $index -lt $expectedNames.Count; $index++) {
    if ($actualNames[$index] -cne $expectedNames[$index]) {
        throw 'The SignPath response contains an unexpected filename.'
    }
}

$signerThumbprint = $null
foreach ($relativePath in $expectedNames) {
    $file = Get-Item -LiteralPath (Join-Path $signedRoot $relativePath)
    $expectedEntry = $prepared.Context.FilesByPath[$relativePath]
    $currentHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ($file.Length -le [long]$expectedEntry.length `
        -or $file.Length -gt ([long]$expectedEntry.length + 4MB) `
        -or $currentHash.Equals(
            [string]$expectedEntry.sha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A SignPath response binary was not changed by signing: $relativePath"
    }

    $contentHash = Get-PeAuthenticodeContentSha256 `
        -Path $file.FullName `
        -UnsignedLength ([long]$expectedEntry.length)
    if (-not $contentHash.Equals(
            [string]$expectedEntry.authenticodeContentSha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A SignPath response binary changed outside Authenticode metadata: $relativePath"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -in @(
            [System.Management.Automation.SignatureStatus]::NotSigned,
            [System.Management.Automation.SignatureStatus]::HashMismatch,
            [System.Management.Automation.SignatureStatus]::NotSupportedFileFormat,
            [System.Management.Automation.SignatureStatus]::Incompatible) `
        -or $null -eq $signature.SignerCertificate) {
        throw "A SignPath response binary has no intact Authenticode signature: $relativePath"
    }
    $publisher = $signature.SignerCertificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
        $false)
    if (-not $publisher.Equals($ExpectedPublisher, [System.StringComparison]::Ordinal) `
        -or -not (Test-CertificateEnhancedKeyUsage `
            -Certificate $signature.SignerCertificate `
            -ObjectId '1.3.6.1.5.5.7.3.3')) {
        throw "A SignPath response binary has an unexpected publisher or signing purpose: $relativePath"
    }
    if ($null -eq $signerThumbprint) {
        $signerThumbprint = $signature.SignerCertificate.Thumbprint
    }
    elseif (-not $signerThumbprint.Equals(
            $signature.SignerCertificate.Thumbprint,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The SignPath response binaries were signed by different certificates.'
    }
}

$incomingRoot = Join-Path $prepared.Context.Root (
    "signed-import-$([Guid]::NewGuid().ToString('N'))")
[System.IO.Directory]::CreateDirectory($incomingRoot) | Out-Null
try {
    foreach ($relativePath in $expectedNames) {
        [System.IO.File]::Copy(
            (Join-Path $signedRoot $relativePath),
            (Join-Path $incomingRoot $relativePath),
            $false)
    }
    foreach ($relativePath in $expectedNames) {
        Move-FileReplacing `
            -SourcePath (Join-Path $incomingRoot $relativePath) `
            -DestinationPath (Join-Path $prepared.Context.Payload $relativePath)
    }
}
finally {
    if ([System.IO.Directory]::Exists($incomingRoot)) {
        [System.IO.Directory]::Delete($incomingRoot, $true)
    }
}

[pscustomobject]@{
    StagingRoot = $prepared.Context.Root
    Payload = $prepared.Context.Payload
    ImportedFileCount = $expectedNames.Count
    Publisher = $ExpectedPublisher
    SignerThumbprint = $signerThumbprint
}
