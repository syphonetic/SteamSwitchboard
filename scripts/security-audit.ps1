[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$RequireExternalScanners
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
    & (Join-Path $PSScriptRoot 'verify.ps1') -Configuration $Configuration

    $vulnerabilityOutput = & dotnet list $solution package `
        --vulnerable `
        --include-transitive `
        --format json
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet package vulnerability audit exited with code $LASTEXITCODE."
    }
    $vulnerabilityReport = $vulnerabilityOutput | ConvertFrom-Json
    $vulnerabilities = @(
        foreach ($project in $vulnerabilityReport.projects) {
            $frameworksProperty = $project.PSObject.Properties['frameworks']
            if ($null -eq $frameworksProperty) {
                continue
            }
            foreach ($framework in $frameworksProperty.Value) {
                $packages = @()
                foreach ($propertyName in @('topLevelPackages', 'transitivePackages')) {
                    $packageProperty = $framework.PSObject.Properties[$propertyName]
                    if ($null -ne $packageProperty) {
                        $packages += @($packageProperty.Value)
                    }
                }
                foreach ($package in $packages) {
                    $vulnerabilityProperty = $package.PSObject.Properties['vulnerabilities']
                    if ($null -ne $vulnerabilityProperty `
                        -and @($vulnerabilityProperty.Value).Count -gt 0) {
                        $package
                    }
                }
            }
        })
    if ($vulnerabilities.Count -ne 0) {
        throw "The dependency audit found $($vulnerabilities.Count) vulnerable package record(s)."
    }
    Write-Host 'NuGet vulnerability audit passed (including transitive packages).'

    $semgrep = Get-Command semgrep -ErrorAction SilentlyContinue
    if ($null -ne $semgrep) {
        $semgrepArguments = @(
            'scan',
            '--config', 'p/csharp',
            '--config', 'p/security-audit',
            '--metrics', 'off',
            '--no-git-ignore',
            '--error'
        )
        $scanTargets = @(
            Get-ChildItem -LiteralPath @(
                (Join-Path $projectRoot 'src'),
                (Join-Path $projectRoot 'tests')) -Filter '*.cs' -File -Recurse |
                Where-Object {
                    $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
                } |
                ForEach-Object { $_.FullName })
        Invoke-Checked $semgrep.Source ($semgrepArguments + $scanTargets)
    }
    elseif ($RequireExternalScanners) {
        throw 'Semgrep is required but was not found.'
    }
    else {
        Write-Warning 'Semgrep was not found; its scan was skipped.'
    }

    $trivy = Get-Command trivy -ErrorAction SilentlyContinue
    if ($null -ne $trivy) {
        Invoke-Checked $trivy.Source @(
            'fs',
            '--scanners', 'vuln,secret,misconfig',
            '--severity', 'HIGH,CRITICAL',
            '--exit-code', '1',
            '--skip-dirs', 'src/SteamSwitchboard.App/bin',
            '--skip-dirs', 'src/SteamSwitchboard.App/obj',
            '--skip-dirs', 'tests/SteamSwitchboard.Tests/bin',
            '--skip-dirs', 'tests/SteamSwitchboard.Tests/obj',
            '--skip-dirs', 'artifacts',
            '.'
        )
    }
    elseif ($RequireExternalScanners) {
        throw 'Trivy is required but was not found.'
    }
    else {
        Write-Warning 'Trivy was not found; its scan was skipped.'
    }
}
finally {
    Pop-Location
}

Write-Host 'SteamSwitchboard security audit passed.'
