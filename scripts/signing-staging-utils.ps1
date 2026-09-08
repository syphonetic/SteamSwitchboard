Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'path-utils.ps1')

function Get-ValidatedSigningStagingRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [switch]$RequireExisting
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($StagingRoot)
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

    if (-not $resolvedRoot.StartsWith($temporaryPrefix, $comparison) `
        -or -not ([System.IO.Path]::GetFileName($resolvedRoot)).StartsWith(
            'SteamSwitchboard-signing-',
            [System.StringComparison]::Ordinal)) {
        throw 'Refusing to use an unexpected signing-staging path.'
    }
    if ($RequireExisting -and -not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw 'The signing-staging directory does not exist.'
    }

    return $resolvedRoot
}

function Assert-ExactJsonPropertySet {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string[]]$ExpectedNames,
        [Parameter(Mandatory)][string]$Label
    )

    $actualNames = @($Value.PSObject.Properties.Name)
    $missing = @($ExpectedNames | Where-Object { $_ -notin $actualNames })
    $unexpected = @($actualNames | Where-Object { $_ -notin $ExpectedNames })
    if ($missing.Count -ne 0 -or $unexpected.Count -ne 0) {
        throw "$Label has missing or unexpected properties."
    }
}

function Test-SafeSigningRelativePath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or $Path.Length -gt 512 `
        -or $Path.Contains('\') `
        -or [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.IndexOf(':') -ge 0 `
        -or $Path.IndexOf([char]0) -ge 0 `
        -or $Path -match '[\x00-\x1F\x7F]') {
        return $false
    }

    $segments = @($Path.Split('/'))
    return $segments.Count -gt 0 `
        -and @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..')
        }).Count -eq 0
}

function Read-SigningStagingManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedSourceRevision,
        [Parameter(Mandatory)][string]$ExpectedUnsignedArchiveSha256
    )

    $resolvedRoot = Get-ValidatedSigningStagingRoot `
        -StagingRoot $StagingRoot `
        -RequireExisting
    $manifestPath = Join-Path $resolvedRoot 'signing-manifest.json'
    $manifestFile = Get-Item -LiteralPath $manifestPath -ErrorAction Stop
    if ($manifestFile.Length -le 0 -or $manifestFile.Length -gt 4MB `
        -or ($manifestFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The signing manifest has an unsafe size or file type.'
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'The signing manifest is malformed.'
    }
    Assert-ExactJsonPropertySet `
        -Value $manifest `
        -ExpectedNames @(
            'schemaVersion',
            'packageName',
            'version',
            'runtime',
            'sourceRevision',
            'sourceArchiveSha256',
            'payloadDirectory',
            'signablePaths',
            'files') `
        -Label 'The signing manifest'

    if (($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) `
        -or [long]$manifest.schemaVersion -ne 1) {
        throw 'The signing manifest schema version is unsupported.'
    }
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' `
        -or $ExpectedSourceRevision -notmatch '^(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$' `
        -or $ExpectedUnsignedArchiveSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'The expected signing metadata is invalid.'
    }

    $expectedPackageName = "SteamSwitchboard-$ExpectedVersion-win-x64"
    if ([string]$manifest.packageName -cne $expectedPackageName `
        -or [string]$manifest.version -cne $ExpectedVersion `
        -or [string]$manifest.runtime -cne 'win-x64' `
        -or [string]$manifest.payloadDirectory -cne 'payload' `
        -or -not ([string]$manifest.sourceRevision).Equals(
            $ExpectedSourceRevision,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not ([string]$manifest.sourceArchiveSha256).Equals(
            $ExpectedUnsignedArchiveSha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The signing manifest does not match the expected release identity.'
    }

    $signablePaths = [string[]]@($manifest.signablePaths)
    $expectedSignablePaths = [string[]]@('SteamSwitchboard.dll', 'SteamSwitchboard.exe')
    [Array]::Sort($signablePaths, [System.StringComparer]::Ordinal)
    if ($signablePaths.Count -ne $expectedSignablePaths.Count) {
        throw 'The signing manifest has an unexpected first-party binary list.'
    }
    for ($index = 0; $index -lt $expectedSignablePaths.Count; $index++) {
        if ($signablePaths[$index] -cne $expectedSignablePaths[$index]) {
            throw 'The signing manifest has an unexpected first-party binary list.'
        }
    }

    $fileEntries = @($manifest.files)
    if ($fileEntries.Count -lt 2 -or $fileEntries.Count -gt 4096) {
        throw 'The signing manifest has an unsafe file count.'
    }
    $seenPaths = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    $lastPath = $null
    foreach ($entry in $fileEntries) {
        Assert-ExactJsonPropertySet `
            -Value $entry `
            -ExpectedNames @('path', 'length', 'sha256', 'authenticodeContentSha256') `
            -Label 'A signing-manifest file entry'
        $relativePath = [string]$entry.path
        if (-not (Test-SafeSigningRelativePath -Path $relativePath) `
            -or $seenPaths.ContainsKey($relativePath)) {
            throw 'The signing manifest contains an unsafe or duplicate file path.'
        }
        if ($null -ne $lastPath `
            -and [System.StringComparer]::Ordinal.Compare($lastPath, $relativePath) -ge 0) {
            throw 'The signing manifest file list is not strictly ordered.'
        }
        if (($entry.length -isnot [int] -and $entry.length -isnot [long]) `
            -or [long]$entry.length -lt 0 `
            -or [long]$entry.length -gt 512MB `
            -or [string]$entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'The signing manifest contains invalid file metadata.'
        }
        $isSignableEntry = $expectedSignablePaths -ccontains $relativePath
        if (($isSignableEntry `
                -and [string]$entry.authenticodeContentSha256 -notmatch '^[0-9A-Fa-f]{64}$') `
            -or (-not $isSignableEntry `
                -and $null -ne $entry.authenticodeContentSha256)) {
            throw 'The signing manifest contains invalid Authenticode content metadata.'
        }
        $seenPaths[$relativePath] = $entry
        $lastPath = $relativePath
    }
    foreach ($signablePath in $expectedSignablePaths) {
        if (-not $seenPaths.ContainsKey($signablePath)) {
            throw "The signing manifest is missing $signablePath."
        }
    }

    [pscustomobject]@{
        Root = $resolvedRoot
        Path = $manifestPath
        Payload = Join-Path $resolvedRoot 'payload'
        PackageName = $expectedPackageName
        Version = $ExpectedVersion
        Runtime = 'win-x64'
        SourceRevision = $ExpectedSourceRevision.ToLowerInvariant()
        SourceArchiveSha256 = $ExpectedUnsignedArchiveSha256.ToLowerInvariant()
        SignablePaths = $expectedSignablePaths
        FilesByPath = $seenPaths
    }
}

function Assert-SigningStagingIntegrity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StagingRoot,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedSourceRevision,
        [Parameter(Mandatory)][string]$ExpectedUnsignedArchiveSha256,
        [ValidateSet('Prepared', 'Signed')]
        [string]$Phase = 'Prepared',
        [string]$ExpectedPublisher
    )

    if ($Phase -eq 'Signed' -and [string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
        throw 'A non-empty expected publisher is required for signed staging.'
    }
    $context = Read-SigningStagingManifest `
        -StagingRoot $StagingRoot `
        -ExpectedVersion $ExpectedVersion `
        -ExpectedSourceRevision $ExpectedSourceRevision `
        -ExpectedUnsignedArchiveSha256 $ExpectedUnsignedArchiveSha256
    if (-not (Test-Path -LiteralPath $context.Payload -PathType Container)) {
        throw 'The signing payload directory is missing.'
    }

    $allItems = @(Get-Item -LiteralPath $context.Payload) + @(
        Get-ChildItem -LiteralPath $context.Payload -Force -Recurse)
    foreach ($item in $allItems) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The signing payload contains a filesystem link.'
        }
    }
    $currentFiles = @(Get-ChildItem -LiteralPath $context.Payload -File -Force -Recurse)
    if ($currentFiles.Count -ne $context.FilesByPath.Count) {
        throw 'The signing payload file inventory changed.'
    }
    $expectedPayloadBytes = [long]0
    foreach ($entry in $context.FilesByPath.Values) {
        $expectedPayloadBytes += [long]$entry.length
    }
    $currentPayloadBytes = [long]0
    foreach ($file in $currentFiles) {
        $currentPayloadBytes += [long]$file.Length
    }
    $maximumSignedPayloadBytes = $expectedPayloadBytes + [long](8MB)
    if (($Phase -eq 'Prepared' -and $currentPayloadBytes -ne $expectedPayloadBytes) `
        -or ($Phase -eq 'Signed' `
            -and ($currentPayloadBytes -lt $expectedPayloadBytes `
                -or $currentPayloadBytes -gt $maximumSignedPayloadBytes))) {
        throw 'The signing payload has an unsafe cumulative size.'
    }

    $seenCurrentPaths = [System.Collections.Generic.Dictionary[string, bool]]::new(
        [System.StringComparer]::Ordinal)
    $signerThumbprint = $null
    foreach ($file in $currentFiles) {
        $relativePath = (Get-ContainedRelativePath `
            -RootPath $context.Payload `
            -CandidatePath $file.FullName).Replace('\', '/')
        if (-not $context.FilesByPath.ContainsKey($relativePath) `
            -or $seenCurrentPaths.ContainsKey($relativePath)) {
            throw 'The signing payload contains an unexpected or duplicate file.'
        }
        $seenCurrentPaths[$relativePath] = $true
        $expectedEntry = $context.FilesByPath[$relativePath]
        $currentHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $isSignable = $context.SignablePaths -ccontains $relativePath

        if (-not $isSignable `
            -and ($file.Length -ne [long]$expectedEntry.length `
                -or -not $currentHash.Equals(
                    [string]$expectedEntry.sha256,
                    [System.StringComparison]::OrdinalIgnoreCase))) {
            throw "A non-signable payload file changed: $relativePath"
        }

        if ($isSignable) {
            $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
            if ($Phase -eq 'Prepared') {
                if ($file.Length -ne [long]$expectedEntry.length `
                    -or -not $currentHash.Equals(
                        [string]$expectedEntry.sha256,
                        [System.StringComparison]::OrdinalIgnoreCase) `
                    -or $signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
                    throw "A prepared first-party binary is altered or already signed: $relativePath"
                }
                $preparedContentHash = Get-PeAuthenticodeContentSha256 `
                    -Path $file.FullName `
                    -UnsignedLength ([long]$expectedEntry.length) `
                    -RequireUnsigned
                if (-not $preparedContentHash.Equals(
                        [string]$expectedEntry.authenticodeContentSha256,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "A prepared first-party binary has altered PE content: $relativePath"
                }
                continue
            }

            if ($currentHash.Equals(
                    [string]$expectedEntry.sha256,
                    [System.StringComparison]::OrdinalIgnoreCase) `
                -or $file.Length -lt [long]$expectedEntry.length `
                -or $file.Length -gt ([long]$expectedEntry.length + 4MB) `
                -or $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid `
                -or $null -eq $signature.SignerCertificate `
                -or $null -eq $signature.TimeStamperCertificate) {
                throw "A first-party binary lacks a valid timestamped signature: $relativePath"
            }
            $signedContentHash = Get-PeAuthenticodeContentSha256 `
                -Path $file.FullName `
                -UnsignedLength ([long]$expectedEntry.length)
            if (-not $signedContentHash.Equals(
                    [string]$expectedEntry.authenticodeContentSha256,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "A signed first-party binary changed outside Authenticode metadata: $relativePath"
            }
            $publisher = $signature.SignerCertificate.GetNameInfo(
                [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
                $false)
            if (-not $publisher.Equals(
                    $ExpectedPublisher,
                    [System.StringComparison]::Ordinal)) {
                throw "A first-party binary has an unexpected publisher: $relativePath"
            }
            $hasCodeSigningEku = Test-CertificateEnhancedKeyUsage `
                -Certificate $signature.SignerCertificate `
                -ObjectId '1.3.6.1.5.5.7.3.3'
            $hasTimestampEku = Test-CertificateEnhancedKeyUsage `
                -Certificate $signature.TimeStamperCertificate `
                -ObjectId '1.3.6.1.5.5.7.3.8'
            if (-not $hasCodeSigningEku -or -not $hasTimestampEku) {
                throw "A first-party binary has an unexpected signing purpose: $relativePath"
            }
            if ($null -eq $signerThumbprint) {
                $signerThumbprint = $signature.SignerCertificate.Thumbprint
            }
            elseif (-not $signerThumbprint.Equals(
                    $signature.SignerCertificate.Thumbprint,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'The first-party binaries were signed by different certificates.'
            }
        }
    }

    [pscustomobject]@{
        Context = $context
        Phase = $Phase
        FileCount = $currentFiles.Count
        Publisher = if ($Phase -eq 'Signed') { $ExpectedPublisher } else { $null }
        SignerThumbprint = $signerThumbprint
    }
}
