[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][string] $WorkDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -Raw (Join-Path $repositoryRoot 'AloeRepl.Wasm/packages.lock.json') | ConvertFrom-Json
$packageId = 'Microsoft.NET.Sdk.WebAssembly.Pack'
$entry = $lock.dependencies.'net10.0'.$packageId
if ($null -eq $entry -or $entry.resolved -notmatch '^\d+\.\d+\.\d+$' -or
    [string]::IsNullOrWhiteSpace($entry.contentHash)) {
    throw 'The WASM package lock entry is missing or unsupported.'
}
$packagePath = [IO.Path]::GetFullPath($PackageDirectory)
$workPath = [IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Force -Path $packagePath, $workPath | Out-Null
$projectPath = Join-Path $workPath 'WasmPackageDownload.csproj'
# A plain SDK project downloads the exact NuGet package before the Blazor SDK can
# resolve the same ID from an installed workload/library-pack source.
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageDownload Include="$packageId" Version="[$($entry.resolved)]" />
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))
& dotnet restore $projectPath --packages $packagePath --source https://api.nuget.org/v3/index.json `
    -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false `
    -p:ManagePackageVersionsCentrally=false -p:NuGetAudit=false -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Official WASM package download failed.' }
$metadataPath = Join-Path $packagePath "$($packageId.ToLowerInvariant())/$($entry.resolved)/.nupkg.metadata"
$metadata = Get-Content -Raw $metadataPath | ConvertFrom-Json
if ($metadata.contentHash -cne $entry.contentHash) {
    throw "Official WASM package contentHash does not match the committed lock file: $metadataPath"
}
Write-Output "Verified $packageId $($entry.resolved) contentHash against the committed lock file."
