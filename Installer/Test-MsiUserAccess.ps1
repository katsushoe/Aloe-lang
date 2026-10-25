[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramW6432 'Aloe'),
    [string]$ReportDirectory,
    [switch]$Execute,
    [switch]$CheckUserOverride,
    [string]$DisposableVmName,
    [switch]$AcknowledgeDisposableVm
)

$ErrorActionPreference = 'Stop'
$report = [ordered]@{ Mode = 'PlanOnly'; Status = 'PlannedOnly'; Checks = @(); Limitations = @('Does not install, repair or uninstall MSI packages.', 'Does not verify an Aloe application process consuming the resulting environment block.') }
$checks = [Collections.Generic.List[string]]::new()
$identity = $null
$restoreUser = $false
$oldUser = $null
$reportCreated = $false

function Test-DirectoryAccess([string]$Directory, [bool]$Writable) {
    $path = Join-Path $Directory ('aloe-access-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $renamed = $path + '.renamed'
    try {
        try {
            $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $stream.Dispose()
        }
        catch [UnauthorizedAccessException] {
            if ($Writable) { throw }
            return
        }
        if (-not $Writable) { throw "Standard user can write to administrator-managed directory: $Directory" }
        [IO.File]::WriteAllText($path, 'first')
        if ([IO.File]::ReadAllText($path) -cne 'first') { throw 'Created file cannot be read.' }
        [IO.File]::WriteAllText($path, 'modified')
        [IO.File]::Move($path, $renamed)
        if ([IO.File]::ReadAllText($renamed) -cne 'modified') { throw 'Modified or renamed file content differs.' }
        [IO.File]::Delete($renamed)
        if ([IO.File]::Exists($renamed)) { throw 'Probe file could not be deleted.' }
    }
    finally {
        # These unique names belong only to this probe; never remove an existing user file.
        foreach ($owned in @($path, $renamed)) {
            if ([IO.File]::Exists($owned)) { [IO.File]::Delete($owned) }
        }
    }
}

try {
    $root = [IO.Path]::GetFullPath($InstallRoot)
    $report['InstallRoot'] = $root
    $report['Plan'] = @('Reject administrator or filtered-administrator tokens', 'Create/read/modify/rename/delete unique files in logs/data', 'Require access-denied when creating a unique file in bin/config')
    if ($CheckUserOverride) { $report['Plan'] += 'Temporarily set user ALOE_INSTALL_ROOT, inspect a fresh Windows user environment block, then restore the original value' }
    if (-not $Execute) { Write-Output ($report | ConvertTo-Json -Depth 6); return }
    $report['Mode'] = 'Execute'
    if (-not $AcknowledgeDisposableVm -or [string]::IsNullOrWhiteSpace($DisposableVmName) -or $DisposableVmName -ine $env:COMPUTERNAME) {
        throw 'Execute requires acknowledgement and the dedicated VM COMPUTERNAME.'
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitProcess) { throw 'Use a 64-bit Windows PowerShell session.' }
    $computer = Get-CimInstance Win32_ComputerSystem
    if (($computer.Manufacturer + ' ' + $computer.Model) -notmatch 'Virtual|VMware|QEMU|KVM|Xen|Parallels') { throw 'A disposable virtual machine is required.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    if ($null -eq $identity.Groups) { throw 'Cannot establish token group membership.' }
    if ($identity.Groups.Contains($administrators) -or ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole($administrators)) {
        throw 'Use an account outside Administrators. A non-elevated administrator session is not a standard-user test.'
    }
    $checks.Add('Token is not an Administrators member (including filtered/deny-only membership)')
    foreach ($relative in @('', 'bin', 'config', 'logs', 'data')) {
        $directory = if ($relative -eq '') { $root } else { Join-Path $root $relative }
        $item = Get-Item -LiteralPath $directory
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Expected ordinary installed directories; reparse points are not accepted.' }
    }
    if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
        $ReportDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('AloeMsiValidation\' + [Guid]::NewGuid().ToString('N'))
    }
    $ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
    if (($ReportDirectory.TrimEnd('\') + '\').StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must be outside the install root.' }
    if (Test-Path -LiteralPath $ReportDirectory) { throw 'Use a new report directory.' }
    New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
    $reportCreated = $true
    foreach ($relative in @('logs', 'data')) {
        Test-DirectoryAccess (Join-Path $root $relative) $true
        $checks.Add("$relative create/read/modify/rename/delete passed")
    }
    foreach ($relative in @('bin', 'config')) {
        Test-DirectoryAccess (Join-Path $root $relative) $false
        $checks.Add("$relative file creation denied")
    }
    if ($CheckUserOverride) {
        $machineValue = [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine')
        if ([string]::IsNullOrEmpty($machineValue)) { throw 'Install must expose a machine ALOE_INSTALL_ROOT before testing precedence.' }
        $oldUser = [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'User')
        $userValue = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('AloeOverride-' + [Guid]::NewGuid().ToString('N'))
        if (-not ('AloeMsiValidation.UserEnvironment' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace AloeMsiValidation {
    public static class UserEnvironment {
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyEnvironmentBlock(IntPtr block);
        public static string Read(IntPtr token, string name) {
            IntPtr block;
            if (!CreateEnvironmentBlock(out block, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                int offset = 0;
                while (true) {
                    string entry = Marshal.PtrToStringUni(IntPtr.Add(block, offset));
                    if (String.IsNullOrEmpty(entry)) return null;
                    if (entry.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return entry.Substring(name.Length + 1);
                    offset += (entry.Length + 1) * 2;
                }
            }
            finally {
                if (!DestroyEnvironmentBlock(block)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
'@
        }
        $restoreUser = $true
        [Environment]::SetEnvironmentVariable('ALOE_INSTALL_ROOT', $userValue, 'User')
        $effective = [AloeMsiValidation.UserEnvironment]::Read($identity.Token, 'ALOE_INSTALL_ROOT')
        if ($effective -cne $userValue) { throw 'Windows user environment block did not select the user override.' }
        if ([Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine') -cne $machineValue) { throw 'Machine override changed during user precedence test.' }
        $checks.Add('Fresh Windows environment block selects user override without changing machine value')
    }
    $report['Status'] = 'StandardUserChecksPassed'
}
catch {
    $report['Status'] = 'FailedOrIncomplete'
    $report['Error'] = $_.Exception.Message
    throw
}
finally {
    try {
        if ($restoreUser) {
            [Environment]::SetEnvironmentVariable('ALOE_INSTALL_ROOT', $oldUser, 'User')
            if ([Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'User') -cne $oldUser) {
                $report['Status'] = 'FailedOrIncomplete'
                throw 'Could not restore the original per-user environment value.'
            }
            $checks.Add('Original user environment value restored')
        }
    }
    finally {
        if ($null -ne $identity) { $identity.Dispose() }
        $report['Checks'] = @($checks.ToArray())
        if ($reportCreated) {
            [IO.File]::WriteAllText((Join-Path $ReportDirectory 'user-access.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
            Write-Output "Report: $ReportDirectory"
        }
    }
}
