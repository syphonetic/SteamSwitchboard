[CmdletBinding()]
param(
    [string]$ScreenshotPath = (Join-Path (
        [System.IO.Path]::GetTempPath()) 'SteamSwitchboard-ui-regression.png')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'tests\SteamSwitchboard.UiRegression\SteamSwitchboard.UiRegression.csproj'
$resolvedScreenshot = [System.IO.Path]::GetFullPath($ScreenshotPath)

Push-Location $projectRoot
try {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore exited with code $LASTEXITCODE."
    }

    & dotnet run `
        --project $project `
        --configuration Release `
        --no-restore `
        -- $resolvedScreenshot
    if ($LASTEXITCODE -ne 0) {
        throw "The UI regression harness exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host "UI regression screenshot: $resolvedScreenshot"
