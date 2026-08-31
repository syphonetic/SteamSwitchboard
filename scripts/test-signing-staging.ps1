[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'signing-staging-utils.ps1')

$testRevision = '0123456789abcdef0123456789abcdef01234567'
$testVersion = '1.0.1'
$testArchiveHash = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoots = [System.Collections.Generic.List[string]]::new()
$signedPayloadRoots = [System.Collections.Generic.List[string]]::new()
$unsignedPeTemplate = Join-Path $temporaryBase (
    "SteamSwitchboard-signing-tests-template-$([Guid]::NewGuid().ToString('N')).dll")
$fixtureNamespace = "SteamSwitchboardSigningFixture$([Guid]::NewGuid().ToString('N'))"
Add-Type `
    -TypeDefinition "namespace $fixtureNamespace { public sealed class Marker { } }" `
    -Language CSharp `
    -OutputAssembly $unsignedPeTemplate
$testCertificate = $null

function New-SigningFixture {
    param([Parameter(Mandatory)][string]$Scenario)

    $root = Join-Path $temporaryBase (
        "SteamSwitchboard-signing-tests-$Scenario-$([Guid]::NewGuid().ToString('N'))")
    $root = Get-ValidatedSigningStagingRoot -StagingRoot $root
    $testRoots.Add($root)
    $payload = Join-Path $root 'payload'
    [System.IO.Directory]::CreateDirectory($payload) | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $payload 'README.md'),
        'fixture documentation',
        [System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $unsignedPeTemplate -Destination (
        Join-Path $payload 'SteamSwitchboard.dll')
    Copy-Item -LiteralPath $unsignedPeTemplate -Destination (
        Join-Path $payload 'SteamSwitchboard.exe')

    $relativePaths = [string[]]@('README.md', 'SteamSwitchboard.dll', 'SteamSwitchboard.exe')
    $fileEntries = @(
        foreach ($relativePath in $relativePaths) {
            $file = Get-Item -LiteralPath (Join-Path $payload $relativePath)
            $authenticodeContentSha256 = if ($relativePath -in @(
                    'SteamSwitchboard.dll',
                    'SteamSwitchboard.exe')) {
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
        packageName = "SteamSwitchboard-$testVersion-win-x64"
        version = $testVersion
        runtime = 'win-x64'
        sourceRevision = $testRevision
        sourceArchiveSha256 = $testArchiveHash
        payloadDirectory = 'payload'
        signablePaths = @('SteamSwitchboard.dll', 'SteamSwitchboard.exe')
        files = $fileEntries
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $root 'signing-manifest.json'),
        (($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    return $root
}

function Invoke-FixtureValidation {
    param(
        [Parameter(Mandatory)][string]$Root,
        [ValidateSet('Prepared', 'Signed')][string]$Phase = 'Prepared'
    )

    $arguments = @{
        StagingRoot = $Root
        ExpectedVersion = $testVersion
        ExpectedSourceRevision = $testRevision
        ExpectedUnsignedArchiveSha256 = $testArchiveHash
        Phase = $Phase
    }
    if ($Phase -eq 'Signed') {
        $arguments.ExpectedPublisher = 'Fixture Publisher'
    }
    Assert-SigningStagingIntegrity @arguments | Out-Null
}

function New-DownloadedSignedPayloadFixture {
    param(
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$SourcePayload
    )

    $root = Join-Path $temporaryBase (
        "SteamSwitchboard-signed-payload-tests-$Scenario-$([Guid]::NewGuid().ToString('N'))")
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    $signedPayloadRoots.Add($root)
    foreach ($file in Get-ChildItem -LiteralPath $SourcePayload -File -Force) {
        Copy-Item -LiteralPath $file.FullName -Destination $root
    }
    return $root
}

function Assert-FixtureRejected {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessagePattern,
        [Parameter(Mandatory)][string]$Scenario
    )

    $message = $null
    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message
    }
    if ([string]::IsNullOrWhiteSpace($message)) {
        throw "Signing-staging validation accepted the $Scenario fixture."
    }
    if ($message -notmatch $ExpectedMessagePattern) {
        throw "Signing-staging validation rejected $Scenario for the wrong reason: $message"
    }
}

try {
    $baseline = New-SigningFixture -Scenario 'baseline'
    Invoke-FixtureValidation -Root $baseline

    $importTarget = New-SigningFixture -Scenario 'import-target'
    $downloadedPayload = New-DownloadedSignedPayloadFixture `
        -Scenario 'baseline' `
        -SourcePayload (Join-Path $importTarget 'payload')
    & (Join-Path $PSScriptRoot 'import-signed-payload.ps1') `
        -StagingRoot $importTarget `
        -SignedPayloadRoot $downloadedPayload `
        -ExpectedVersion $testVersion `
        -ExpectedSourceRevision $testRevision `
        -ExpectedUnsignedArchiveSha256 $testArchiveHash | Out-Null
    Invoke-FixtureValidation -Root $importTarget

    $importCountTarget = New-SigningFixture -Scenario 'import-count-target'
    $extraPayload = New-DownloadedSignedPayloadFixture `
        -Scenario 'extra-file' `
        -SourcePayload (Join-Path $importCountTarget 'payload')
    [System.IO.File]::WriteAllText(
        (Join-Path $extraPayload 'injected.txt'),
        'injected',
        [System.Text.UTF8Encoding]::new($false))
    Assert-FixtureRejected `
        -Action {
            & (Join-Path $PSScriptRoot 'import-signed-payload.ps1') `
                -StagingRoot $importCountTarget `
                -SignedPayloadRoot $extraPayload `
                -ExpectedVersion $testVersion `
                -ExpectedSourceRevision $testRevision `
                -ExpectedUnsignedArchiveSha256 $testArchiveHash | Out-Null
        } `
        -ExpectedMessagePattern 'unexpected file count' `
        -Scenario 'signed-payload file-count change'

    $authenticodeLayout = New-SigningFixture -Scenario 'authenticode-layout'
    $authenticodeFile = Join-Path $authenticodeLayout 'payload\SteamSwitchboard.exe'
    $unsignedLength = (Get-Item -LiteralPath $authenticodeFile).Length
    $unsignedContentHash = Get-PeAuthenticodeContentSha256 `
        -Path $authenticodeFile `
        -UnsignedLength $unsignedLength `
        -RequireUnsigned
    $testCertificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject 'CN=SteamSwitchboard Signing Layout Test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter ([DateTime]::UtcNow.AddDays(1))
    $layoutSignature = Set-AuthenticodeSignature `
        -LiteralPath $authenticodeFile `
        -Certificate $testCertificate `
        -HashAlgorithm SHA256
    if ($layoutSignature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw 'The Authenticode layout fixture was not signed.'
    }
    $signedContentHash = Get-PeAuthenticodeContentSha256 `
        -Path $authenticodeFile `
        -UnsignedLength $unsignedLength
    if ($signedContentHash -cne $unsignedContentHash) {
        throw 'Authenticode content normalisation changed after a real signature was appended.'
    }
    $signedBytes = [System.IO.File]::ReadAllBytes($authenticodeFile)
    $signedBytes[2] = $signedBytes[2] -bxor 1
    [System.IO.File]::WriteAllBytes($authenticodeFile, $signedBytes)
    $mutatedContentHash = Get-PeAuthenticodeContentSha256 `
        -Path $authenticodeFile `
        -UnsignedLength $unsignedLength
    if ($mutatedContentHash -ceq $unsignedContentHash) {
        throw 'Authenticode content normalisation ignored a non-signature binary mutation.'
    }

    $changedDocumentation = New-SigningFixture -Scenario 'changed-doc'
    $changedDocumentationPath = Join-Path $changedDocumentation 'payload\README.md'
    $changedDocumentationBytes = [System.IO.File]::ReadAllBytes(
        $changedDocumentationPath)
    $changedDocumentationBytes[0] = $changedDocumentationBytes[0] -bxor 1
    [System.IO.File]::WriteAllBytes(
        $changedDocumentationPath,
        $changedDocumentationBytes)
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $changedDocumentation } `
        -ExpectedMessagePattern 'non-signable payload file changed' `
        -Scenario 'changed non-signable file'

    $expandedPayload = New-SigningFixture -Scenario 'expanded-payload'
    Add-Content -LiteralPath (
        Join-Path $expandedPayload 'payload\README.md') -Value 'oversized'
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $expandedPayload } `
        -ExpectedMessagePattern 'unsafe cumulative size' `
        -Scenario 'expanded cumulative payload'

    $addedFile = New-SigningFixture -Scenario 'added-file'
    [System.IO.File]::WriteAllText(
        (Join-Path $addedFile 'payload\injected.dll'),
        'injected',
        [System.Text.UTF8Encoding]::new($false))
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $addedFile } `
        -ExpectedMessagePattern 'file inventory changed' `
        -Scenario 'added file'

    $changedBinary = New-SigningFixture -Scenario 'changed-binary'
    $changedBinaryPath = Join-Path $changedBinary 'payload\SteamSwitchboard.exe'
    $changedBinaryBytes = [System.IO.File]::ReadAllBytes($changedBinaryPath)
    $changedBinaryBytes[2] = $changedBinaryBytes[2] -bxor 1
    [System.IO.File]::WriteAllBytes($changedBinaryPath, $changedBinaryBytes)
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $changedBinary } `
        -ExpectedMessagePattern 'altered or already signed' `
        -Scenario 'altered prepared binary'
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $changedBinary -Phase Signed } `
        -ExpectedMessagePattern 'valid timestamped signature' `
        -Scenario 'unsigned final binary'

    $wrongRevision = New-SigningFixture -Scenario 'wrong-revision'
    $wrongRevisionManifest = Get-Content -LiteralPath (
        Join-Path $wrongRevision 'signing-manifest.json') -Raw | ConvertFrom-Json
    $wrongRevisionManifest.sourceRevision = '1123456789abcdef0123456789abcdef01234567'
    [System.IO.File]::WriteAllText(
        (Join-Path $wrongRevision 'signing-manifest.json'),
        (($wrongRevisionManifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $wrongRevision } `
        -ExpectedMessagePattern 'does not match the expected release identity' `
        -Scenario 'source-revision substitution'

    $traversal = New-SigningFixture -Scenario 'traversal'
    $traversalManifest = Get-Content -LiteralPath (
        Join-Path $traversal 'signing-manifest.json') -Raw | ConvertFrom-Json
    $traversalManifest.files[0].path = '../README.md'
    [System.IO.File]::WriteAllText(
        (Join-Path $traversal 'signing-manifest.json'),
        (($traversalManifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    Assert-FixtureRejected `
        -Action { Invoke-FixtureValidation -Root $traversal } `
        -ExpectedMessagePattern 'unsafe or duplicate file path' `
        -Scenario 'manifest traversal'
}
finally {
    if ($null -ne $testCertificate) {
        Remove-Item -LiteralPath (
            "Cert:\CurrentUser\My\$($testCertificate.Thumbprint)") -Force
    }
    foreach ($root in $testRoots) {
        $validatedRoot = Get-ValidatedSigningStagingRoot -StagingRoot $root
        if (Test-Path -LiteralPath $validatedRoot) {
            Remove-Item -LiteralPath $validatedRoot -Recurse -Force
        }
    }
    foreach ($root in $signedPayloadRoots) {
        if ([System.IO.Directory]::Exists($root)) {
            [System.IO.Directory]::Delete($root, $true)
        }
    }
    if (Test-Path -LiteralPath $unsignedPeTemplate) {
        [System.IO.File]::Delete($unsignedPeTemplate)
    }
}

Write-Host 'Signing-staging adversarial tests passed.'
