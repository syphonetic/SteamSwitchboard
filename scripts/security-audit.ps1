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

function Test-CleanLockedRestore {
    $temporaryBase = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetTempPath())
    $temporaryPrefix = $temporaryBase.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $cleanCache = [System.IO.Path]::GetFullPath((Join-Path $temporaryBase (
        "SteamSwitchboard-nuget-clean-$([Guid]::NewGuid().ToString('N'))")))
    $hasExpectedName = [System.IO.Path]::GetFileName($cleanCache).StartsWith(
        'SteamSwitchboard-nuget-clean-',
        [System.StringComparison]::Ordinal)
    if (-not $cleanCache.StartsWith(
            $temporaryPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not $hasExpectedName) {
        throw 'Refusing to use an unexpected clean-package-cache path.'
    }

    $previousPackageCache = [System.Environment]::GetEnvironmentVariable(
        'NUGET_PACKAGES',
        [System.EnvironmentVariableTarget]::Process)
    try {
        [System.IO.Directory]::CreateDirectory($cleanCache) | Out-Null
        [System.Environment]::SetEnvironmentVariable(
            'NUGET_PACKAGES',
            $cleanCache,
            [System.EnvironmentVariableTarget]::Process)
        Invoke-Checked 'dotnet' @(
            'restore',
            $solution,
            '--locked-mode',
            '--no-http-cache',
            '--force')
    }
    finally {
        [System.Environment]::SetEnvironmentVariable(
            'NUGET_PACKAGES',
            $previousPackageCache,
            [System.EnvironmentVariableTarget]::Process)
        try {
            # The clean restore writes package-folder locations into obj assets.
            # Regenerate those files against the normal cache before continuing.
            Invoke-Checked 'dotnet' @(
                'restore',
                $solution,
                '--locked-mode',
                '--force')
        }
        finally {
            if ([System.IO.Directory]::Exists($cleanCache)) {
                [System.IO.Directory]::Delete($cleanCache, $true)
            }
        }
    }

    Write-Host 'Clean-cache locked NuGet restore passed.'
}

Push-Location $projectRoot
try {
    Test-CleanLockedRestore
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
        $temporaryBase = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::GetTempPath())
        $semgrepWorkingDirectory = [System.IO.Path]::GetFullPath((
            Join-Path $temporaryBase (
                "SteamSwitchboard-semgrep-$([Guid]::NewGuid().ToString('N'))")))
        $temporaryPrefix = $temporaryBase.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $semgrepWorkingDirectory.StartsWith(
                $temporaryPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) `
            -or -not [System.IO.Path]::GetFileName(
                $semgrepWorkingDirectory).StartsWith(
                    'SteamSwitchboard-semgrep-',
                    [System.StringComparison]::Ordinal)) {
            throw 'Refusing to use an unexpected Semgrep working directory.'
        }

        [System.IO.Directory]::CreateDirectory(
            $semgrepWorkingDirectory) | Out-Null
        Push-Location $semgrepWorkingDirectory
        try {
            Invoke-Checked $semgrep.Source ($semgrepArguments + $scanTargets)
        }
        finally {
            Pop-Location
            if ([System.IO.Directory]::Exists($semgrepWorkingDirectory)) {
                [System.IO.Directory]::Delete(
                    $semgrepWorkingDirectory,
                    $true)
            }
        }
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
            '--skip-dirs', 'tests/SteamSwitchboard.UiRegression/bin',
            '--skip-dirs', 'tests/SteamSwitchboard.UiRegression/obj',
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
