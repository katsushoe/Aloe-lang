param([string]$MsiPath)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repository 'Installer\Test-MsiLifecycle.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors.Message -join '; ') }
# Load function definitions only: never run the script's installation entry point.
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$passed = 0
function Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "FAIL $Label" }
    $script:passed++
}
function Reject([scriptblock]$Action, [string]$Label) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Check $failed $Label
}
function New-Identity([bool]$Upgrade = $false) {
    [pscustomobject]@{
        Path = $(if ($Upgrade) { 'C:\Package Files\upgrade.msi' } else { 'C:\Package Files\initial.msi' });
        Sha256 = $(if ($Upgrade) { 'B' * 64 } else { 'A' * 64 });
        ProductName = 'Aloe-lang'; Manufacturer = 'Akatsukisoft';
        UpgradeCode = '{60A2445F-3183-4842-9170-3C9E72A75C93}'; ALLUSERS = '1'; Template = 'x64;1041';
        ProductVersion = $(if ($Upgrade) { '0.1.1' } else { '0.1.0' });
        ProductCode = $(if ($Upgrade) { '{A14C84BA-65D2-46B3-A310-A08898B94B8E}' } else { '{0FDC5E40-27FB-4891-997D-255CA29F47E6}' });
        PackageCode = $(if ($Upgrade) { '{A14C84BA-65D2-46B3-A310-A08898B94B80}' } else { '{0FDC5E40-27FB-4891-997D-255CA29F47E0}' })
    }
}
$initial = New-Identity
$upgrade = New-Identity $true
Assert-MsiLifecyclePair $initial $upgrade
Check $true 'valid pair'
foreach ($case in @(
    @('ProductName', 'Other'), @('Manufacturer', 'Other'), @('UpgradeCode', [Guid]::NewGuid().ToString()),
    @('ALLUSERS', '2'), @('Template', 'Intel;1041'), @('ProductVersion', '0.1.0'),
    @('ProductVersion', '0.0.9'), @('ProductVersion', '0.1.1.2'), @('ProductVersion', '256.1.1'),
    @('ProductVersion', '0.256.1'), @('ProductVersion', '0.1.65536'),
    @('ProductCode', $initial.ProductCode), @('ProductCode', [Guid]::Empty.ToString()),
    @('PackageCode', $initial.PackageCode), @('PackageCode', [Guid]::Empty.ToString()), @('Sha256', $initial.Sha256)
)) {
    $candidate = New-Identity $true
    $candidate.($case[0]) = $case[1]
    Reject { Assert-MsiLifecyclePair $initial $candidate } ('invalid pair: ' + $case[0] + '=' + $case[1])
}
Reject { Assert-DisposableVmRequest '' $false } 'no VM acknowledgement'
Reject { Assert-DisposableVmRequest '' $true } 'no VM name'
Reject { Assert-DisposableVmRequest ('different-' + $env:COMPUTERNAME) $true } 'wrong VM name'
Reject { Quote-MsiLifecycleArgument 'bad"argument' } 'quote rejection'
Reject { Quote-MsiLifecycleArgument "bad`nargument" } 'newline rejection'
$work = Join-Path ([IO.Path]::GetTempPath()) ('AloeMsiLifecycleContracts-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$plan = @(New-MsiLifecyclePlan $initial $upgrade 'C:\Program Files\Aloe' $work)
Check ($plan.Count -eq 5) 'five lifecycle commands'
Check ($plan[0].Arguments.Contains('/i "C:\Package Files\initial.msi"')) 'quoted install source'
Check ($plan[0].Arguments.Contains('INSTALLFOLDER="C:\Program Files\Aloe"')) 'quoted install root'
Check ($plan[1].Arguments.StartsWith('/fa "' + $initial.ProductCode + '"')) 'repair by product code'
Check ($plan[3].ExpectedExitCode -eq 1603) 'downgrade expected failure'
Check ($plan[4].Arguments.StartsWith('/x "' + $upgrade.ProductCode + '"')) 'uninstall by upgrade product code'
$minimalPlan = @(New-MsiLifecyclePlan $initial $upgrade 'C:\Program Files\Aloe' $work -SkipDowngrade)
Check ($minimalPlan.Count -eq 4) 'minimal lifecycle has four commands'
Check (($minimalPlan.Name -join ',') -ceq 'install,repair,upgrade,uninstall') 'minimal lifecycle order'
Check (@($minimalPlan | Where-Object Name -eq 'downgrade-rejected').Count -eq 0) 'minimal lifecycle omits downgrade'
Check ($minimalPlan[-1].Arguments.StartsWith('/x "' + $upgrade.ProductCode + '"')) 'minimal lifecycle still uninstalls upgraded product'
Check (@($minimalPlan | Where-Object ExpectedExitCode -ne 0).Count -eq 0) 'minimal lifecycle requires success for each command'
Check (@($plan | Where-Object { -not $_.Arguments.Contains('/qn /norestart /l*v') }).Count -eq 0) 'silent no-restart logging'
$script:ProcessCalls = [Collections.Generic.List[object]]::new()
$script:NextExitCode = 0
function Start-Process {
    param($FilePath, $ArgumentList, $WindowStyle, [switch]$Wait, [switch]$PassThru)
    $script:ProcessCalls.Add(@{ File = $FilePath; Arguments = $ArgumentList; Hidden = $WindowStyle; Wait = $Wait.IsPresent })
    [pscustomobject]@{ ExitCode = $script:NextExitCode }
}
$results = [Collections.Generic.List[object]]::new()
Set-Content -LiteralPath $plan[0].Log -Value 'mock log'
Invoke-MsiLifecycleStep $plan[0] $results
Check ($results.Count -eq 1 -and $ProcessCalls.Count -eq 1 -and $ProcessCalls[0].Hidden -eq 'Hidden' -and $ProcessCalls[0].Wait) 'mocked invocation and report'
foreach ($exitCode in @(3010, 1641, 1603)) {
    $script:NextExitCode = $exitCode
    Reject { Invoke-MsiLifecycleStep $plan[0] $results } ('unexpected/reboot exit ' + $exitCode)
}
$script:NextExitCode = 1603
Set-Content -LiteralPath $plan[3].Log -Value 'mock downgrade log'
Invoke-MsiLifecycleStep $plan[3] $results
Check ($results[$results.Count - 1].ExitCode -eq 1603) 'expected downgrade failure records exit'
$script:NextExitCode = 0
Reject { Invoke-MsiLifecycleStep $plan[1] $results } 'missing log rejected'
$fixture = Join-Path $work 'fixture'
foreach ($directory in @('bin\AloeCli', 'config', 'logs', 'data')) { New-Item -ItemType Directory -Path (Join-Path $fixture $directory) -Force | Out-Null }
$binary = Join-Path $fixture 'bin\AloeCli\aloec.exe'
Set-Content -LiteralPath $binary -Value 'binary fixture'
Set-Content -LiteralPath (Join-Path $fixture 'config\aloec.json') -Value '{}'
Set-Content -LiteralPath (Join-Path $fixture 'LICENSE') -Value 'license fixture'
$hashes = Get-LifecycleFileHashes $fixture
Assert-LifecycleFileHashes $fixture $hashes
Check ($hashes.Count -eq 3) 'payload hashes'
Set-Content -LiteralPath $binary -Value 'corrupt fixture'
Reject { Assert-LifecycleFileHashes $fixture $hashes } 'corrupt file rejected'
Set-Content -LiteralPath $binary -Value 'binary fixture'
Set-Content -LiteralPath (Join-Path $fixture 'config\extra.json') -Value '{}'
Reject { Assert-LifecycleFileHashes $fixture $hashes } 'extra payload file rejected'
foreach ($directory in @('logs', 'data')) { [IO.File]::WriteAllText((Join-Path (Join-Path $fixture $directory) 'sentinel.txt'), 'retained') }
Assert-LifecycleSentinels $fixture 'sentinel.txt' 'retained'
Check $true 'data sentinels'
[IO.File]::WriteAllText((Join-Path $fixture 'data\sentinel.txt'), 'changed')
Reject { Assert-LifecycleSentinels $fixture 'sentinel.txt' 'retained' } 'changed sentinel rejected'
if (-not [string]::IsNullOrWhiteSpace($MsiPath)) {
    $before = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $identity = Read-MsiIdentity $installer $MsiPath
        Check ($identity.ProductName -ceq 'Aloe-lang' -and $identity.Template -match '^x64;' -and $identity.ALLUSERS -eq '1') 'real MSI read-only identity'
        Check ([Guid]$identity.PackageCode -ne [Guid]::Empty) 'real package code'
        Check ($before -ceq (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash) 'MSI unchanged after read'
    }
    finally { Close-MsiComObject $installer }
}
Write-Output "MSI lifecycle contracts: $passed/$passed passed; msiexec calls were mocked."
