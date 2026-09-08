Set-StrictMode -Version Latest

function Get-ContainedRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$CandidatePath
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)
    $resolvedCandidate = [System.IO.Path]::GetFullPath($CandidatePath)
    $comparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }

    if ($resolvedCandidate.Equals($resolvedRoot, $comparison)) {
        return [string]::Empty
    }

    $rootPrefix = $resolvedRoot
    if (-not $rootPrefix.EndsWith(
            [System.IO.Path]::DirectorySeparatorChar.ToString(),
            [System.StringComparison]::Ordinal) `
        -and -not $rootPrefix.EndsWith(
            [System.IO.Path]::AltDirectorySeparatorChar.ToString(),
            [System.StringComparison]::Ordinal)) {
        $rootPrefix += [System.IO.Path]::DirectorySeparatorChar
    }

    if (-not $resolvedCandidate.StartsWith($rootPrefix, $comparison)) {
        throw "The path '$resolvedCandidate' must stay inside '$resolvedRoot'."
    }

    return $resolvedCandidate.Substring($rootPrefix.Length)
}

function Move-FileReplacing {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    if ([System.IO.File]::Exists($DestinationPath)) {
        $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
        $backupPath = Join-Path `
            ([System.IO.Path]::GetDirectoryName($resolvedDestination)) `
            ".$([System.IO.Path]::GetFileName($resolvedDestination)).$([Guid]::NewGuid().ToString('N')).replace-backup"
        $replaceSucceeded = $false
        try {
            [System.IO.File]::Replace($SourcePath, $resolvedDestination, $backupPath)
            $replaceSucceeded = $true
        }
        finally {
            if ($replaceSucceeded -and [System.IO.File]::Exists($backupPath)) {
                [System.IO.File]::Delete($backupPath)
            }
        }
    }
    else {
        [System.IO.File]::Move($SourcePath, $DestinationPath)
    }
}

function New-DeterministicArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$RootName,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    $resolvedSource = (Resolve-Path -LiteralPath $SourceDirectory).Path
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
    if ([string]::IsNullOrWhiteSpace($RootName) `
        -or $RootName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 `
        -or $RootName -in @('.', '..')) {
        throw 'The archive root name is invalid.'
    }
    if ([System.IO.File]::Exists($resolvedDestination)) {
        throw "The archive destination already exists: $resolvedDestination"
    }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $relativePaths = [string[]]@(
        Get-ChildItem -LiteralPath $resolvedSource -File -Recurse |
            ForEach-Object {
                if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing to archive a filesystem link: $($_.FullName)"
                }
                Get-ContainedRelativePath `
                    -RootPath $resolvedSource `
                    -CandidatePath $_.FullName
            })
    [Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)

    $stream = [System.IO.File]::Open(
        $resolvedDestination,
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

            $inputStream = [System.IO.File]::Open(
                (Join-Path $resolvedSource $relativePath),
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            $output = $entry.Open()
            try {
                $inputStream.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $inputStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $stream.Dispose()
    }
}

function Test-CertificateEnhancedKeyUsage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$ObjectId
    )

    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($usage in $extension.EnhancedKeyUsages) {
                if ($usage.Value -eq $ObjectId) {
                    return $true
                }
            }
        }
    }
    return $false
}

function Get-PeAuthenticodeContentSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [long]$UnsignedLength = -1,
        [switch]$RequireUnsigned
    )

    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.Length -lt 256 -or $file.Length -gt 64MB) {
        throw 'A first-party PE file has an unsafe size.'
    }
    if ($UnsignedLength -lt 0) {
        $UnsignedLength = $file.Length
    }
    if ($UnsignedLength -lt 256 -or $UnsignedLength -gt $file.Length) {
        throw 'The expected unsigned PE length is invalid.'
    }

    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw 'A first-party binary is not a valid PE image.'
    }
    $peOffset = [System.BitConverter]::ToUInt32($bytes, 0x3C)
    if ($peOffset -gt ($UnsignedLength - 256) `
        -or $bytes[$peOffset] -ne 0x50 `
        -or $bytes[$peOffset + 1] -ne 0x45 `
        -or $bytes[$peOffset + 2] -ne 0 `
        -or $bytes[$peOffset + 3] -ne 0) {
        throw 'A first-party binary has an invalid PE header.'
    }

    $optionalHeader = [long]$peOffset + 24
    $optionalHeaderSize = [System.BitConverter]::ToUInt16($bytes, [int]$peOffset + 20)
    if ($optionalHeaderSize -lt 136 `
        -or ($optionalHeader + $optionalHeaderSize) -gt $UnsignedLength) {
        throw 'A first-party binary has an invalid optional header.'
    }
    $magic = [System.BitConverter]::ToUInt16($bytes, [int]$optionalHeader)
    $dataDirectory = if ($magic -eq 0x10B) {
        $optionalHeader + 96
    }
    elseif ($magic -eq 0x20B) {
        $optionalHeader + 112
    }
    else {
        throw 'A first-party binary has an unsupported PE format.'
    }
    $checksumOffset = $optionalHeader + 64
    $certificateDirectoryOffset = $dataDirectory + 32
    if (($certificateDirectoryOffset + 8) -gt ($optionalHeader + $optionalHeaderSize) `
        -or ($certificateDirectoryOffset + 8) -gt $UnsignedLength) {
        throw 'A first-party binary has an invalid certificate directory.'
    }

    $certificateOffset = [long][System.BitConverter]::ToUInt32(
        $bytes,
        [int]$certificateDirectoryOffset)
    $certificateSize = [long][System.BitConverter]::ToUInt32(
        $bytes,
        [int]$certificateDirectoryOffset + 4)
    if (($certificateOffset -eq 0) -ne ($certificateSize -eq 0)) {
        throw 'A first-party binary has an inconsistent certificate table.'
    }
    if ($RequireUnsigned -and ($certificateOffset -ne 0 -or $certificateSize -ne 0)) {
        throw 'A prepared first-party binary already has a certificate table.'
    }
    if (-not $RequireUnsigned) {
        $alignedUnsignedLength = ($UnsignedLength + 7) -band (-bnot 7)
        if ($certificateOffset -lt $UnsignedLength `
            -or $certificateOffset -gt $alignedUnsignedLength `
            -or $certificateOffset % 8 -ne 0 `
            -or $certificateSize -lt 8 `
            -or ($certificateOffset + $certificateSize) -ne $file.Length) {
            throw 'A signed first-party binary has an unexpected certificate-table layout.'
        }
        for ($index = $UnsignedLength; $index -lt $certificateOffset; $index++) {
            if ($bytes[$index] -ne 0) {
                throw 'A signed first-party binary has nonzero pre-certificate padding.'
            }
        }
    }

    $normalised = [System.IO.MemoryStream]::new([int]$UnsignedLength)
    try {
        $normalised.Write($bytes, 0, [int]$checksumOffset)
        $afterChecksum = $checksumOffset + 4
        $normalised.Write(
            $bytes,
            [int]$afterChecksum,
            [int]($certificateDirectoryOffset - $afterChecksum))
        $afterCertificateDirectory = $certificateDirectoryOffset + 8
        $normalised.Write(
            $bytes,
            [int]$afterCertificateDirectory,
            [int]($UnsignedLength - $afterCertificateDirectory))
        $normalised.Position = 0
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString(
                $sha256.ComputeHash($normalised))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $normalised.Dispose()
    }
}

function Test-ReleaseProductVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductVersion,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [string]$ExpectedSourceRevision
    )

    if ($ProductVersion.Length -gt 128 -or $ExpectedVersion.Length -gt 128) {
        return $false
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceRevision)) {
        if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
                $ExpectedSourceRevision,
                '\A(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})\z',
                [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            return $false
        }

        return $ProductVersion.Equals(
            "$ExpectedVersion+$ExpectedSourceRevision",
            [System.StringComparison]::OrdinalIgnoreCase)
    }

    if ($ProductVersion.Equals(
            $ExpectedVersion,
            [System.StringComparison]::Ordinal)) {
        return $true
    }

    $metadataPrefix = "$ExpectedVersion+"
    if (-not $ProductVersion.StartsWith(
            $metadataPrefix,
            [System.StringComparison]::Ordinal)) {
        return $false
    }

    $metadata = $ProductVersion.Substring($metadataPrefix.Length)
    return [System.Text.RegularExpressions.Regex]::IsMatch(
        $metadata,
        '\A[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*\z',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
}
