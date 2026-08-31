[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArchivePath,

    [string]$ChecksumPath,

    [string]$ExpectedVersion,

    [string]$ExpectedSourceRevision,

    [string]$ExpectedPublisher,

    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'path-utils.ps1')

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    [xml]$projectXml = Get-Content -LiteralPath (
        Join-Path $projectRoot 'src\SteamSwitchboard.App\SteamSwitchboard.App.csproj') -Raw
    $ExpectedVersion = @(
        $projectXml.Project.PropertyGroup |
            ForEach-Object { [string]$_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
}
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'The expected package version is invalid.'
}

if ([string]::IsNullOrWhiteSpace($ExpectedSourceRevision)) {
    $sourceRevisionOutput = @(& git -C $projectRoot rev-parse --verify HEAD)
    if ($LASTEXITCODE -ne 0 -or $sourceRevisionOutput.Count -ne 1) {
        throw 'The expected source revision is missing and could not be read from Git.'
    }

    $ExpectedSourceRevision = $sourceRevisionOutput[0].Trim()
}
if ($ExpectedSourceRevision -notmatch '^(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$') {
    throw 'The expected source revision must be a complete Git object ID.'
}

function Assert-SafeLocalFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.StartsWith('\\', [System.StringComparison]::Ordinal) `
        -or $Path.StartsWith('\\?\', [System.StringComparison]::Ordinal) `
        -or $Path.StartsWith('\\.\', [System.StringComparison]::Ordinal)) {
        throw 'Package inputs must use ordinary local paths.'
    }

    $root = [System.IO.Path]::GetPathRoot($Path)
    $driveType = [System.IO.DriveInfo]::new($root).DriveType
    if ($driveType -notin @(
            [System.IO.DriveType]::Fixed,
            [System.IO.DriveType]::Removable)) {
        throw 'Package inputs must use a local fixed or removable drive.'
    }

    $current = $root
    $relative = Get-ContainedRelativePath -RootPath $root -CandidatePath $Path
    foreach ($component in $relative.Split(
        @([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
        [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $component
        $attributes = (Get-Item -LiteralPath $current -Force).Attributes
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Package inputs cannot pass through filesystem links.'
        }
    }
}

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ([string]::IsNullOrWhiteSpace($ChecksumPath)) {
    $ChecksumPath = "$archive.sha256"
}
$checksum = (Resolve-Path -LiteralPath $ChecksumPath).Path
Assert-SafeLocalFile -Path $archive
Assert-SafeLocalFile -Path $checksum

$expectedRootName = "SteamSwitchboard-$ExpectedVersion-win-x64"
$expectedArchiveName = "$expectedRootName.zip"
$checksumText = (Get-Content -LiteralPath $checksum -Raw).Trim()
if ($checksumText -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<name>[^\r\n]+)$' `
    -or $Matches.name -cne $expectedArchiveName) {
    throw 'The package checksum file is malformed.'
}
$expectedHash = $Matches.hash.ToLowerInvariant()

$archiveLock = [System.IO.File]::Open(
    $archive,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::Read)
try {
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw 'Package checksum mismatch.'
}

$archiveBytes = (Get-Item -LiteralPath $archive).Length
if ($archiveBytes -gt 256MB) {
    throw 'The package archive is unexpectedly large.'
}

$expectedEntries = @(
    "$expectedRootName/SteamSwitchboard.exe",
    "$expectedRootName/SteamSwitchboard.dll",
    "$expectedRootName/SteamSwitchboard.runtimeconfig.json",
    "$expectedRootName/README.md",
    "$expectedRootName/LICENSE",
    "$expectedRootName/NOTICE.md",
    "$expectedRootName/SECURITY.md",
    "$expectedRootName/CHANGELOG.md",
    "$expectedRootName/CONTRIBUTING.md",
    "$expectedRootName/THIRD-PARTY-LICENSES/DOTNET-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/DOTNET-THIRD-PARTY-NOTICES.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WEBVIEW2-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WEBVIEW2-NOTICE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-APP-SDK-BASE-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-APP-SDK-BASE-NOTICE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-APP-SDK-FOUNDATION-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-APP-SDK-INTERACTIVE-EXPERIENCES-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-APP-SDK-RUNTIME-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-APP-SDK-RUNTIME-NOTICE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-SDK-LICENSE.txt",
    "$expectedRootName/THIRD-PARTY-LICENSES/MICROSOFT-WINDOWS-SDK-NOTICE.txt",
    "$expectedRootName/docs/ARCHITECTURE.md",
    "$expectedRootName/docs/GITHUB_RELEASE.md",
    "$expectedRootName/docs/PRIVACY.md",
    "$expectedRootName/docs/VALIDATION.md",
    "$expectedRootName/artifacts/ui-final.png",
    "$expectedRootName/Assets/Branding/SteamSwitchboard-app-logo.png",
    "$expectedRootName/src/SteamSwitchboard.App/Assets/Branding/SteamSwitchboard-logo-v1.png"
)

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archiveStream = [System.IO.File]::Open(
    $archive,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::Read)
$zip = [System.IO.Compression.ZipArchive]::new(
    $archiveStream,
    [System.IO.Compression.ZipArchiveMode]::Read,
    $false)
$entryNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$presentEntries = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
$rootNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
$entryCount = 0
$totalUncompressedBytes = [long]0

try {
    foreach ($entry in $zip.Entries) {
        $entryCount++
        if ($entryCount -gt 5000) {
            throw 'The package contains too many archive entries.'
        }

        $name = $entry.FullName
        if ([string]::IsNullOrWhiteSpace($name) `
            -or $name.Length -gt 512 `
            -or $name.Contains('\') `
            -or $name.StartsWith('/', [System.StringComparison]::Ordinal) `
            -or $name.IndexOfAny([char[]](0..31)) -ge 0 `
            -or -not $entryNames.Add($name)) {
            throw "The package contains an unsafe or duplicate entry name."
        }

        $trimmedName = $name.TrimEnd('/')
        $segments = $trimmedName.Split('/')
        if ($segments.Count -eq 0 `
            -or $segments[0] -ne $expectedRootName `
            -or @($segments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) `
                        -or $_ -in @('.', '..') `
                        -or $_.Contains(':') `
                        -or $_.EndsWith(' ', [System.StringComparison]::Ordinal) `
                        -or $_.EndsWith('.', [System.StringComparison]::Ordinal)
                }).Count -gt 0) {
            throw 'The package contains an unsafe path or an unexpected root folder.'
        }

        foreach ($segment in $segments) {
            $deviceStem = ($segment -split '\.', 2)[0]
            if ($deviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                throw 'The package contains a reserved Windows device path.'
            }
        }

        $null = $rootNames.Add($segments[0])
        $unixFileType = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($unixFileType -eq 0xA000) {
            throw 'The package contains a symbolic link.'
        }

        if ($entry.Length -gt 256MB) {
            throw 'The package contains an unexpectedly large file.'
        }
        if ($entry.Length -gt 0 `
            -and ($entry.CompressedLength -le 0 `
                -or ([double]$entry.Length / [double]$entry.CompressedLength) -gt 100)) {
            throw 'The package contains a suspiciously compressed entry.'
        }
        $totalUncompressedBytes += $entry.Length
        if ($totalUncompressedBytes -gt 512MB) {
            throw 'The package expands beyond the allowed size.'
        }

        $relativeSegments = @($segments | Select-Object -Skip 1)
        $leaf = $segments[-1]
        if (@($relativeSegments | Where-Object {
                    $_ -in @('BrowserData', 'Logs')
                }).Count -gt 0 `
            -or $leaf -ieq 'state.json' `
            -or [System.IO.Path]::GetExtension($leaf) -in @('.pdb', '.user', '.suo')) {
            throw 'The package contains debug output or local session data.'
        }

        $null = $presentEntries.Add($name)
    }
}
finally {
    $zip.Dispose()
    $archiveStream.Dispose()
}

if ($rootNames.Count -ne 1 -or -not $rootNames.Contains($expectedRootName)) {
    throw 'The package must contain exactly one expected root folder.'
}
foreach ($requiredEntry in $expectedEntries) {
    if (-not $presentEntries.Contains($requiredEntry)) {
        throw "The package is missing required entry: $requiredEntry"
    }
}

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$extractRoot = Join-Path $temporaryBase "SteamSwitchboard-validate-$([Guid]::NewGuid().ToString('N'))"
$resolvedExtract = [System.IO.Path]::GetFullPath($extractRoot)
$temporaryPrefix = $temporaryBase.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedExtract.StartsWith(
        $temporaryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase) `
    -or -not ([System.IO.Path]::GetFileName($resolvedExtract)).StartsWith(
        'SteamSwitchboard-validate-',
        [System.StringComparison]::Ordinal)) {
    throw 'Refusing to use an unexpected validation path.'
}

$result = $null
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $resolvedExtract
    $packageRoot = Join-Path $resolvedExtract $expectedRootName
    $executable = Join-Path $packageRoot 'SteamSwitchboard.exe'
    $applicationDll = Join-Path $packageRoot 'SteamSwitchboard.dll'
    $notificationLogo = Join-Path $packageRoot (
        'Assets\Branding\SteamSwitchboard-app-logo.png')

    $drawingAssembly = if ($PSVersionTable.PSEdition -eq 'Desktop') {
        'System.Drawing'
    }
    else {
        'System.Drawing.Common'
    }
    Add-Type -AssemblyName $drawingAssembly

    $notificationLogoHash = (
        Get-FileHash -LiteralPath $notificationLogo -Algorithm SHA256).Hash
    if ($notificationLogoHash -cne (
            'B684FFBB817F43B3992B44D06EAA04DBFCADFA4CBDD1F2A86572317F4FB59993')) {
        throw 'The packaged notification logo does not match the reviewed SteamSwitchboard artwork.'
    }
    $notificationLogoImage = [System.Drawing.Image]::FromFile($notificationLogo)
    try {
        if ($notificationLogoImage.Width -ne 512 `
            -or $notificationLogoImage.Height -ne 512) {
            throw 'The packaged notification logo has unexpected dimensions.'
        }
        $notificationLogoSize = "$($notificationLogoImage.Width)x$($notificationLogoImage.Height)"
    }
    finally {
        $notificationLogoImage.Dispose()
    }

    $pdbCount = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.pdb' -File -Recurse).Count
    if ($pdbCount -ne 0) {
        throw 'The package contains debug symbol files.'
    }

    $version = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version) `
        -or -not (Test-ReleaseProductVersion `
            -ProductVersion $version `
            -ExpectedVersion $ExpectedVersion `
            -ExpectedSourceRevision $ExpectedSourceRevision)) {
        throw "The packaged version '$version' does not match version '$ExpectedVersion' from source '$ExpectedSourceRevision'."
    }
    $applicationVersion = (Get-Item -LiteralPath $applicationDll).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($applicationVersion) `
        -or -not (Test-ReleaseProductVersion `
            -ProductVersion $applicationVersion `
            -ExpectedVersion $ExpectedVersion `
            -ExpectedSourceRevision $ExpectedSourceRevision)) {
        throw "The packaged application DLL version '$applicationVersion' does not match version '$ExpectedVersion' from source '$ExpectedSourceRevision'."
    }

    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($executable)
    try {
        if ($null -eq $icon -or $icon.Width -lt 16 -or $icon.Height -lt 16) {
            throw 'The packaged executable does not contain a usable icon.'
        }
        $iconSize = "$($icon.Width)x$($icon.Height)"
    }
    finally {
        if ($null -ne $icon) {
            $icon.Dispose()
        }
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    if ($RequireSignature) {
        foreach ($firstPartyBinary in @($executable, $applicationDll)) {
            $binarySignature = Get-AuthenticodeSignature -LiteralPath $firstPartyBinary
            if ($binarySignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
                throw "A required first-party signature is invalid: $firstPartyBinary"
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher) `
                -and $binarySignature.SignerCertificate.GetNameInfo(
                    [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
                    $false) -ne $ExpectedPublisher) {
                throw "A first-party binary has an unexpected publisher: $firstPartyBinary"
            }
        }
    }

    $result = [pscustomobject]@{
        Archive = $archive
        PackageRootName = $expectedRootName
        ArchiveBytes = $archiveBytes
        UncompressedBytes = $totalUncompressedBytes
        EntryCount = $entryCount
        Sha256 = $actualHash
        ExecutableVersion = $version
        ApplicationDllVersion = $applicationVersion
        SourceRevision = $ExpectedSourceRevision.ToLowerInvariant()
        EmbeddedIcon = $iconSize
        NotificationLogo = $notificationLogoSize
        SignatureStatus = [string]$signature.Status
        RequiredSignature = [bool]$RequireSignature
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedExtract) {
        Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
    }
}

if (Test-Path -LiteralPath $resolvedExtract) {
    throw 'Package validation could not clean its temporary extraction folder.'
}

$result
}
finally {
    $archiveLock.Dispose()
}
