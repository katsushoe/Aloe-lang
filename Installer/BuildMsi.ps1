param(
    [string]$ProductVersion = '0.1.0',
    [string]$ProductCode = '',
    [string]$RuntimeFrameworkVersion = '10.0.13',
    [switch]$FrameworkDependent,
    [switch]$OfflineRestore,
    [switch]$SkipMsiValidation
)

$ErrorActionPreference = 'Stop'

if ($RuntimeFrameworkVersion -notmatch '^10\.0\.\d+$') {
    throw 'RuntimeFrameworkVersion must be a .NET 10 servicing version, for example 10.0.13.'
}

if ($ProductVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'ProductVersion must have exactly three numeric fields, for example 0.1.0.'
}

if ([string]::IsNullOrWhiteSpace($ProductCode)) {
    if ($ProductVersion -ne '0.1.0') {
        throw 'A new ProductCode is required for each major-upgrade package after version 0.1.0.'
    }
    $ProductCode = '{0FDC5E40-27FB-4891-997D-255CA29F47E6}'
}

$parsedProductCode = [Guid]::Empty
if (-not [Guid]::TryParse($ProductCode, [ref]$parsedProductCode)) {
    throw 'ProductCode must be a GUID.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$payloadParent = Join-Path $repositoryRoot '.local\installer\staging'
$outputParent = Join-Path $repositoryRoot '.local\installer\out'
$nugetPackageCache = Join-Path $env:USERPROFILE '.nuget\packages'
$workRoot = Join-Path $payloadParent 'current'
$payloadRoot = Join-Path $workRoot 'payload'
$installRoot = Join-Path $workRoot 'install-root'
$stagingMarker = Join-Path $workRoot '.generated-by-buildmsi'

New-Item -ItemType Directory -Path $payloadParent -Force | Out-Null
if (Test-Path -LiteralPath $workRoot) {
    $resolvedStagingParent = (Resolve-Path $payloadParent).Path.TrimEnd('\') + '\'
    $resolvedWorkRoot = (Resolve-Path $workRoot).Path
    if (-not $resolvedWorkRoot.StartsWith($resolvedStagingParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $stagingMarker -PathType Leaf) -or
        (Get-Content -Raw -LiteralPath $stagingMarker) -ne 'Aloe-lang generated installer staging data') {
        throw "Refusing to replace unrecognized staging data at '$workRoot'."
    }
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
Set-Content -LiteralPath $stagingMarker -Value 'Aloe-lang generated installer staging data' -NoNewline

try {
    $applications = @(
        @{ Name = 'AloeCli'; Project = 'AloeCli\AloeCli.csproj' },
        @{ Name = 'AloeVmHost'; Project = 'AloeVmHost\AloeVmHost.csproj' },
        @{ Name = 'Aloe2WasmHost'; Project = 'Aloe2WasmHost\Aloe2WasmHost.csproj' },
        @{ Name = 'AloeRepl.Native'; Project = 'AloeRepl.Native\AloeRepl.Native.csproj' },
        @{ Name = 'AloeSample'; Project = 'AloeSample\AloeSample.csproj' }
    )

    foreach ($application in $applications) {
        $destination = Join-Path $payloadRoot $application.Name
        $projectPath = Join-Path $repositoryRoot $application.Project
        $restoreArguments = @(
            'restore', $projectPath, '--runtime', 'win-x64',
            "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion",
            "-p:AloeInstallRoot=$installRoot", '-p:NuGetAudit=false', '-m:1', '-nr:false'
        )
        if ($OfflineRestore) {
            $restoreArguments += @('--source', $nugetPackageCache)
        }
        & dotnet @restoreArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for $($application.Name) with exit code $LASTEXITCODE."
        }

        $publishArguments = @(
            'publish', $projectPath,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--output', $destination,
            '--no-restore',
            "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion",
            '-p:PublishTrimmed=false',
            '-p:DebugType=None',
            "-p:AloeInstallRoot=$installRoot",
            '-m:1', '-nr:false'
        )
        if ($FrameworkDependent) {
            $publishArguments += '-p:SelfContained=false'
        } else {
            $publishArguments += '-p:SelfContained=true'
        }

        & dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $($application.Name) with exit code $LASTEXITCODE."
        }

        $runtimeConfigs = @(Get-ChildItem -LiteralPath $destination -Filter '*.runtimeconfig.json' -File)
        if ($runtimeConfigs.Count -ne 1) {
            throw "Expected one runtime configuration for $($application.Name)."
        }
        $runtimeConfig = Get-Content -LiteralPath $runtimeConfigs[0].FullName -Raw | ConvertFrom-Json
        $frameworks = if ($FrameworkDependent) {
            @($runtimeConfig.runtimeOptions.framework)
        } else {
            @($runtimeConfig.runtimeOptions.includedFrameworks)
        }
        if (@($frameworks | Where-Object {
            $_.name -eq 'Microsoft.NETCore.App' -and $_.version -eq $RuntimeFrameworkVersion
        }).Count -ne 1) {
            throw "Published runtime version mismatch for $($application.Name)."
        }
    }

    $payloadFiles = Get-ChildItem -LiteralPath $payloadRoot -File -Recurse
    if ($payloadFiles.Count -eq 0) {
        throw 'The publish step produced an empty payload.'
    }

    $installerProject = Join-Path $PSScriptRoot 'Aloe.Installer.wixproj'
    $installerRestoreArguments = @('restore', $installerProject, '-p:NuGetAudit=false', '-m:1', '-nr:false')
    if ($OfflineRestore) {
        $installerRestoreArguments += @('--source', $nugetPackageCache)
    }
    & dotnet @installerRestoreArguments
    if ($LASTEXITCODE -ne 0) {
        throw "WiX package restore failed with exit code $LASTEXITCODE."
    }

    $installerArguments = @(
        'build', $installerProject,
        '--configuration', 'Release',
        '--no-restore',
        "-p:ProductVersion=$ProductVersion",
        "-p:ProductCode=$ProductCode",
        "-p:PayloadRoot=$payloadRoot"
    )
    if ($SkipMsiValidation) {
        $installerArguments += '-p:SuppressValidation=true'
    }

    & dotnet @installerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI build failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ((Test-Path -LiteralPath $stagingMarker -PathType Leaf) -and
        (Get-Content -Raw -LiteralPath $stagingMarker) -eq 'Aloe-lang generated installer staging data') {
        $resolvedStagingParent = (Resolve-Path $payloadParent).Path.TrimEnd('\') + '\'
        $resolvedWorkRoot = (Resolve-Path $workRoot).Path
        if (-not $resolvedWorkRoot.StartsWith($resolvedStagingParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove staging data outside the installer staging directory.'
        }
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}
