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
