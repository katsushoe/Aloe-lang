[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InitialMsi,
    [Parameter(Mandatory = $true)][string]$UpgradeMsi,
    [string]$ReportDirectory,
    [switch]$Execute,
    [string]$DisposableVmName,
    [switch]$AcknowledgeDisposableVm,
    [string]$ExpectedMachineOverride,
    [switch]$PauseForUserAcceptance
)

$ErrorActionPreference = 'Stop'

function Invoke-MsiComMember($Target, [string]$Name, [Reflection.BindingFlags]$Flags, [object[]]$Arguments) {
    $result = $Target.GetType().InvokeMember($Name, $Flags, $null, $Target, $Arguments)
    return ,$result
}

function Close-MsiComObject($Value) {
    if ($null -ne $Value -and [Runtime.InteropServices.Marshal]::IsComObject($Value)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)
    }
}

function Read-MsiIdentity($Installer, [string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or [IO.Path]::GetExtension($resolved) -ine '.msi') {
        throw 'Each input must be an existing MSI file.'
    }
    $database = $null
    $summary = $null
    try {
        # Mode zero is read-only. No MSI is opened as an installation session.
        $database = Invoke-MsiComMember $Installer 'OpenDatabase' 'InvokeMethod' @($resolved, 0)
        $values = [ordered]@{ Path = $resolved; Sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash }
        foreach ($property in @('ProductName', 'Manufacturer', 'ProductVersion', 'ProductCode', 'UpgradeCode', 'ALLUSERS')) {
            $view = $null
            $record = $null
            try {
                $query = 'SELECT `Value` FROM `Property` WHERE `Property` = ''' + $property + ''''
                $view = Invoke-MsiComMember $database 'OpenView' 'InvokeMethod' @($query)
                Invoke-MsiComMember $view 'Execute' 'InvokeMethod' @() | Out-Null
                $record = Invoke-MsiComMember $view 'Fetch' 'InvokeMethod' @()
                if ($null -eq $record) { throw "MSI property '$property' is missing." }
                $values[$property] = [string](Invoke-MsiComMember $record 'StringData' 'GetProperty' @(1))
                Invoke-MsiComMember $view 'Close' 'InvokeMethod' @() | Out-Null
            }
            finally { Close-MsiComObject $record; Close-MsiComObject $view }
        }
        $summary = Invoke-MsiComMember $database 'SummaryInformation' 'GetProperty' @(0)
        $values['Template'] = [string](Invoke-MsiComMember $summary 'Property' 'GetProperty' @(7))
        $values['PackageCode'] = [string](Invoke-MsiComMember $summary 'Property' 'GetProperty' @(9))
        [pscustomobject]$values
    }
    finally { Close-MsiComObject $summary; Close-MsiComObject $database }
}

function Assert-MsiLifecyclePair($Initial, $Upgrade) {
    $family = [Guid]'{60A2445F-3183-4842-9170-3C9E72A75C93}'
    foreach ($package in @($Initial, $Upgrade)) {
        if ($package.ProductName -cne 'Aloe-lang' -or $package.Manufacturer -cne 'Akatsukisoft' -or
            [Guid]$package.UpgradeCode -ne $family -or $package.ALLUSERS -ne '1' -or
            $package.Template -notmatch '^x64(?:;|$)') { throw 'Expected the Aloe-lang all-user x64 product family.' }
        if ($package.ProductVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'ProductVersion must have exactly three fields.' }
        $version = [version]$package.ProductVersion
        if ($version.Major -gt 255 -or $version.Minor -gt 255 -or $version.Build -gt 65535) { throw 'ProductVersion exceeds Windows Installer field limits.' }
        if ([Guid]$package.ProductCode -eq [Guid]::Empty -or [Guid]$package.PackageCode -eq [Guid]::Empty) {
            throw 'ProductCode and PackageCode must be nonempty GUIDs.'
        }
    }
    if ([version]$Upgrade.ProductVersion -le [version]$Initial.ProductVersion) { throw 'Upgrade version must be higher.' }
    if ([Guid]$Initial.ProductCode -eq [Guid]$Upgrade.ProductCode) { throw 'A major upgrade requires a new ProductCode.' }
    if ([Guid]$Initial.PackageCode -eq [Guid]$Upgrade.PackageCode -or $Initial.Sha256 -eq $Upgrade.Sha256) {
        throw 'Inputs must be distinct MSI builds with unique PackageCodes.'
    }
}

function Assert-DisposableVmRequest([string]$Name, [bool]$Acknowledged) {
    if (-not $Acknowledged -or [string]::IsNullOrWhiteSpace($Name) -or $Name -ine $env:COMPUTERNAME) {
        throw 'Execute requires -AcknowledgeDisposableVm and -DisposableVmName matching this dedicated disposable VM.'
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitProcess) {
        throw 'Execute requires 64-bit Windows PowerShell or PowerShell on Windows.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Execute requires an elevated administrator session inside the disposable VM.'
        }
    }
    finally { $identity.Dispose() }
    $computer = Get-CimInstance Win32_ComputerSystem
    if (($computer.Manufacturer + ' ' + $computer.Model) -notmatch 'Virtual|VMware|QEMU|KVM|Xen|Parallels') {
        throw 'The machine does not identify as a supported virtual machine. Do not use a physical workstation.'
    }
}

function Quote-MsiLifecycleArgument([string]$Value) {
    if ($Value -match '["\r\n]') { throw 'MSI arguments cannot contain quotes or line breaks.' }
    '"' + $Value + '"'
}

function New-MsiLifecyclePlan($Initial, $Upgrade, [string]$Root, [string]$Reports) {
    @(
        @{ Name = 'install'; Switch = '/i'; Target = $Initial.Path; ExitCode = 0; SetRoot = $true },
        @{ Name = 'repair'; Switch = '/fa'; Target = $Initial.ProductCode; ExitCode = 0; SetRoot = $false },
        @{ Name = 'upgrade'; Switch = '/i'; Target = $Upgrade.Path; ExitCode = 0; SetRoot = $true },
        @{ Name = 'downgrade-rejected'; Switch = '/i'; Target = $Initial.Path; ExitCode = 1603; SetRoot = $true },
        @{ Name = 'uninstall'; Switch = '/x'; Target = $Upgrade.ProductCode; ExitCode = 0; SetRoot = $false }
    ) | ForEach-Object {
        $log = Join-Path $Reports ($_.Name + '.log')
        $arguments = $_.Switch + ' ' + (Quote-MsiLifecycleArgument $_.Target) + ' /qn /norestart /l*v ' + (Quote-MsiLifecycleArgument $log)
        if ($_.SetRoot) { $arguments += ' INSTALLFOLDER=' + (Quote-MsiLifecycleArgument $Root) }
        [pscustomobject]@{ Name = $_.Name; Arguments = $arguments; ExpectedExitCode = $_.ExitCode; Log = $log }
    }
}

function Invoke-MsiLifecycleStep($Step, $Results) {
    $started = Get-Date
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $Step.Arguments -WindowStyle Hidden -Wait -PassThru
    $Results.Add([pscustomobject]@{ Name = $Step.Name; ExitCode = $process.ExitCode; ExpectedExitCode = $Step.ExpectedExitCode; Log = $Step.Log; Started = $started.ToString('o'); Finished = (Get-Date).ToString('o') })
    if ($process.ExitCode -eq 3010 -or $process.ExitCode -eq 1641) {
        throw 'Windows Installer requires a reboot. This run is incomplete; preserve its report and revert the VM snapshot before a fresh run.'
    }
    if ($process.ExitCode -ne $Step.ExpectedExitCode) { throw "Step '$($Step.Name)' returned $($process.ExitCode); see '$($Step.Log)'." }
    if (-not (Test-Path -LiteralPath $Step.Log -PathType Leaf)) { throw "Missing MSI log for '$($Step.Name)'." }
}

function Get-LifecycleFileHashes([string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $hashes = @{}
    foreach ($directory in @('bin', 'config')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Root $directory) -File -Recurse) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Unexpected payload file outside the installation root.'
            }
            $hashes[$file.FullName.Substring($prefix.Length)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $hashes['LICENSE'] = (Get-FileHash -LiteralPath (Join-Path $Root 'LICENSE') -Algorithm SHA256).Hash
    $hashes
}

function Assert-LifecycleFileHashes([string]$Root, $Expected) {
    $actual = Get-LifecycleFileHashes $Root
    if ($actual.Count -ne $Expected.Count) { throw 'Installed payload file count changed unexpectedly.' }
    foreach ($relative in $Expected.Keys) {
        if ($actual[$relative] -cne $Expected[$relative]) { throw "Installed payload hash mismatch: $relative" }
    }
}

function Assert-LifecycleProductState($Installer, [string]$Code, [int]$Expected) {
    $actual = [int](Invoke-MsiComMember $Installer 'ProductState' 'GetProperty' @($Code))
    if ($actual -ne $Expected) { throw "Unexpected product state for ${Code}: $actual; expected $Expected." }
}

function Assert-LifecycleSentinels([string]$Root, [string]$Name, [string]$Content) {
    foreach ($directory in @('logs', 'data')) {
        if ([IO.File]::ReadAllText((Join-Path (Join-Path $Root $directory) $Name)) -cne $Content) { throw 'User data sentinel changed.' }
    }
}

function Assert-LifecycleRegistryRemoved($Installer, $Initial, $Upgrade) {
    $related = $null
    try {
        $related = Invoke-MsiComMember $Installer 'RelatedProducts' 'GetProperty' @($Initial.UpgradeCode)
        if ([int](Invoke-MsiComMember $related 'Count' 'GetProperty' @()) -ne 0) { throw 'Aloe product-family registration survived uninstall.' }
    }
    finally { Close-MsiComObject $related }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        try {
            foreach ($path in @('SOFTWARE\Akatsukisoft\Aloe-lang',
                ('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $Initial.ProductCode),
                ('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $Upgrade.ProductCode))) {
                $key = $machine.OpenSubKey($path)
                if ($null -ne $key) { $key.Dispose(); throw "Installer registration survived uninstall: $view / $path" }
            }
        }
        finally { $machine.Dispose() }
    }
}

if ($Execute) { Assert-DisposableVmRequest $DisposableVmName $AcknowledgeDisposableVm.IsPresent }
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) ('.local\msi-lifecycle\' + [Guid]::NewGuid().ToString('N'))
}
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
$installRoot = Join-Path $env:ProgramW6432 'Aloe'
$installPrefix = [IO.Path]::GetFullPath($installRoot).TrimEnd('\') + '\'
if (($reportRoot.TrimEnd('\') + '\').StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must be outside the install root.' }
if (Test-Path -LiteralPath $reportRoot) { throw 'Use a new report directory for each run.' }
$installer = $null
$report = [ordered]@{ Mode = $(if ($Execute) { 'Execute' } else { 'PlanOnly' }); Status = 'Preparing'; ComputerName = $env:COMPUTERNAME; InstallRoot = $installRoot; Steps = @(); Checks = @(); Remaining = @('Standard-user effective ACL/write tests', 'Existing machine and per-user ALOE_INSTALL_ROOT precedence on separate clean VM snapshots') }
$report['ExpectedMachineOverride'] = $ExpectedMachineOverride
$report['PauseForUserAcceptance'] = $PauseForUserAcceptance.IsPresent
$results = [Collections.Generic.List[object]]::new()
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $initial = Read-MsiIdentity $installer $InitialMsi
    $upgrade = Read-MsiIdentity $installer $UpgradeMsi
    Assert-MsiLifecyclePair $initial $upgrade
    foreach ($package in @($initial, $upgrade)) {
        if ($package.Path.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'MSI sources must be outside the install root.' }
    }
    $report['Initial'] = $initial
    $report['Upgrade'] = $upgrade
    $plan = @(New-MsiLifecyclePlan $initial $upgrade $installRoot $reportRoot)
    $report['Plan'] = $plan
    New-Item -ItemType Directory -Path $reportRoot | Out-Null
    if (-not $Execute) {
        $report['Status'] = 'PlannedOnly'
        Write-Output 'Plan only: no installation, repair, or removal was performed.'
    }
    else {
        if (Test-Path -LiteralPath $installRoot) { throw 'Execute requires a clean VM snapshot with no existing Aloe install root.' }
        $beforeMachine = [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine')
        if ([string]::IsNullOrEmpty($ExpectedMachineOverride)) {
            if ($null -ne $beforeMachine) { throw 'Baseline requires no machine override; for a separate preservation run supply -ExpectedMachineOverride matching its existing value.' }
        }
        elseif ($beforeMachine -cne $ExpectedMachineOverride) { throw 'Existing machine override does not exactly match -ExpectedMachineOverride.' }
        if ($null -ne [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'User')) { throw 'The administrator runner requires no per-user Aloe override; run user precedence with the separate standard-user probe.' }
        $related = $null
        try {
            $related = Invoke-MsiComMember $installer 'RelatedProducts' 'GetProperty' @($initial.UpgradeCode)
            if ([int](Invoke-MsiComMember $related 'Count' 'GetProperty' @()) -ne 0) { throw 'Existing Aloe products are registered; revert to a clean VM snapshot.' }
        }
        finally { Close-MsiComObject $related }
        Assert-LifecycleProductState $installer $initial.ProductCode -1
        Assert-LifecycleProductState $installer $upgrade.ProductCode -1
        Invoke-MsiLifecycleStep $plan[0] $results
        Assert-LifecycleProductState $installer $initial.ProductCode 5
        foreach ($hostPath in @('AloeCli\aloec.exe', 'AloeVmHost\aloevm.exe', 'Aloe2WasmHost\aloe2wasm.exe', 'AloeRepl.Native\aloe-repl.exe', 'AloeSample\AloeSample.exe')) {
            if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $installRoot 'bin') $hostPath) -PathType Leaf)) { throw "Missing installed host: $hostPath" }
        }
        $ownedRoot = [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine')
        if ([string]::IsNullOrEmpty($ExpectedMachineOverride)) {
            if ([string]::IsNullOrWhiteSpace($ownedRoot) -or $ownedRoot.TrimEnd('\') -ine $installRoot.TrimEnd('\')) { throw 'Installed machine environment root is incorrect.' }
        }
        elseif ($ownedRoot -cne $ExpectedMachineOverride) { throw 'Install replaced the pre-existing machine environment override.' }
        $baseline = Get-LifecycleFileHashes $installRoot
        $report['InitialPayloadHashes'] = $baseline
        if ($PauseForUserAcceptance) {
            [void](Read-Host 'Installed. Switch to a true standard user and run Test-MsiUserAccess.ps1; save its report outside the install root. Return here and press Enter only when finished')
        }
        $sentinelName = 'aloe-lifecycle-' + [Guid]::NewGuid().ToString('N') + '.txt'
        $sentinelContent = [Guid]::NewGuid().ToString('N')
        foreach ($directory in @('logs', 'data')) {
            [IO.File]::WriteAllText((Join-Path (Join-Path $installRoot $directory) $sentinelName), $sentinelContent)
        }
        $repairTarget = Join-Path $installRoot 'bin\AloeCli\aloec.exe'
        if (-not ([IO.Path]::GetFullPath($repairTarget)).StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid repair target.' }
        Remove-Item -LiteralPath $repairTarget -Force
        Invoke-MsiLifecycleStep $plan[1] $results
        Assert-LifecycleFileHashes $installRoot $baseline
        Assert-LifecycleSentinels $installRoot $sentinelName $sentinelContent
        if ([Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine') -cne $ownedRoot) { throw 'Repair changed the owned machine environment value.' }
        Invoke-MsiLifecycleStep $plan[2] $results
        Assert-LifecycleProductState $installer $initial.ProductCode -1
        Assert-LifecycleProductState $installer $upgrade.ProductCode 5
        Assert-LifecycleSentinels $installRoot $sentinelName $sentinelContent
        if ([Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine') -cne $ownedRoot) { throw 'Upgrade changed the owned machine environment value.' }
        $upgraded = Get-LifecycleFileHashes $installRoot
        $report['UpgradedPayloadHashes'] = $upgraded
        Invoke-MsiLifecycleStep $plan[3] $results
        if (-not (Select-String -LiteralPath $plan[3].Log -SimpleMatch 'A newer version of Aloe-lang is already installed.' -Quiet)) { throw 'Downgrade failed without the expected version rejection message.' }
        Assert-LifecycleProductState $installer $initial.ProductCode -1
        Assert-LifecycleProductState $installer $upgrade.ProductCode 5
        Assert-LifecycleFileHashes $installRoot $upgraded
        Invoke-MsiLifecycleStep $plan[4] $results
        Assert-LifecycleProductState $installer $initial.ProductCode -1
        Assert-LifecycleProductState $installer $upgrade.ProductCode -1
        foreach ($relative in $upgraded.Keys) {
            if (Test-Path -LiteralPath (Join-Path $installRoot $relative)) { throw "Installer-owned file survived uninstall: $relative" }
        }
        Assert-LifecycleSentinels $installRoot $sentinelName $sentinelContent
        $afterMachine = [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine')
        if ([string]::IsNullOrEmpty($ExpectedMachineOverride)) {
            if ($null -ne $afterMachine) { throw 'Installer-owned machine environment value survived uninstall.' }
        }
        elseif ($afterMachine -cne $ExpectedMachineOverride) { throw 'Uninstall changed the pre-existing machine environment override.' }
        Assert-LifecycleRegistryRemoved $installer $initial $upgrade
        $report['Checks'] = @('First install and five host files', 'Full payload repair hashes', 'Major upgrade registration', 'Downgrade rejection and unchanged payload', 'Uninstall owned-file removal', 'Logs/data sentinel preservation', 'Owned machine environment create/remove')
        $report['Checks'] += 'Product-family, ownership and uninstall registration removal (64/32-bit views)'
        if (-not [string]::IsNullOrEmpty($ExpectedMachineOverride)) {
            $report['Checks'] = @($report['Checks'] | Where-Object { $_ -ne 'Owned machine environment create/remove' }) + 'Pre-existing machine environment preserved across install/repair/upgrade/uninstall'
        }
        $report['Status'] = 'LifecyclePassedAdditionalAcceptancePending'
        Write-Output 'Baseline lifecycle passed. Effective standard-user ACL and override precedence acceptance remains separate.'
    }
}
catch {
    $report['Status'] = 'FailedOrIncomplete'
    $report['Error'] = $_.Exception.Message
    throw
}
finally {
    $report['Steps'] = @($results.ToArray())
    if (Test-Path -LiteralPath $reportRoot -PathType Container) {
        [IO.File]::WriteAllText((Join-Path $reportRoot 'report.json'), ($report | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        Write-Output "Report: $reportRoot"
    }
    Close-MsiComObject $installer
}
