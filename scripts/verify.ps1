[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $projectRoot 'SteamSwitchboard.sln'

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'generate-icon.ps1')
    Invoke-Checked dotnet @('restore', $solution, '--locked-mode')
    Invoke-Checked dotnet @(
        'format', $solution,
        '--verify-no-changes',
        '--no-restore',
        '--verbosity', 'minimal'
    )
    Invoke-Checked dotnet @(
        'build',
        $solution,
        '--configuration', $Configuration,
        '--no-restore',
        '--no-incremental'
    )
    Invoke-Checked dotnet @(
        'test',
        $solution,
        '--configuration', $Configuration,
        '--no-build',
        '--no-restore'
    )
    & (Join-Path $PSScriptRoot 'test-signing-staging.ps1')
}
finally {
    Pop-Location
}

Write-Host "SteamSwitchboard verification passed ($Configuration)."
