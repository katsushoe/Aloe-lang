[CmdletBinding()]
param(
    [string] $ReportDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repositoryRoot '.local\dependency-audit'
}
$runName = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $ReportDirectory $runName
New-Item -ItemType Directory -Path $runDirectory | Out-Null

$projects = @(
    @{ Path = 'Aloe.sln'; Report = 'solution.audit.json' },
    @{ Path = 'Installer\Aloe.Installer.wixproj'; Report = 'installer.audit.json' }
)

foreach ($entry in $projects) {
    $projectPath = Join-Path $repositoryRoot $entry.Path
    & dotnet restore $projectPath --locked-mode -p:ContinuousIntegrationBuild=true `
        -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore or audit failed for $($entry.Path) with exit code $LASTEXITCODE."
    }

    $auditJson = (& dotnet list $projectPath package --vulnerable --include-transitive --no-restore --format json) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "Vulnerability inventory failed for $($entry.Path) with exit code $LASTEXITCODE."
    }
    $reportPath = Join-Path $runDirectory $entry.Report
    [IO.File]::WriteAllText($reportPath, $auditJson, [Text.UTF8Encoding]::new($false))
    $audit = $auditJson | ConvertFrom-Json
    if ($audit.version -ne 1 -or @($audit.projects).Count -eq 0 -or @($audit.sources).Count -eq 0) {
        throw "Incomplete vulnerability inventory for $($entry.Path)."
    }
    if (@($audit.problems | Where-Object { $null -ne $_ }).Count -gt 0 -or
        @($audit.projects | Where-Object { @($_.problems | Where-Object { $null -ne $_ }).Count -gt 0 }).Count -gt 0) {
        throw "Vulnerability inventory reported problems for $($entry.Path); see $reportPath."
    }

    $moderate = 0
    $blocking = 0
    foreach ($project in $audit.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in (@($framework.topLevelPackages) + @($framework.transitivePackages))) {
                foreach ($finding in $package.vulnerabilities) {
                    if ($finding.severity -notin @('Low', 'Moderate', 'High', 'Critical')) {
                        throw "Unrecognized vulnerability severity in $reportPath."
                    }
                    if ($finding.severity -in @('High', 'Critical')) { $blocking++ }
                    elseif ($finding.severity -eq 'Moderate') { $moderate++ }
                }
            }
        }
    }
    if ($blocking -gt 0) {
        throw "$blocking high/critical vulnerability findings in $reportPath."
    }
    if ($moderate -gt 0) {
        Write-Warning "$moderate moderate findings in $reportPath require a recorded disposition before release."
    }
    Write-Output "Validated locked restore and audit: $($entry.Path); report: $reportPath"
}
