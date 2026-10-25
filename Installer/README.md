# Windows MSI

The WiX 7 project builds an x64, per-machine MSI for the command-line compiler, VM host, Aloe-to-WASM host, native REPL, and sample. Each application is published self-contained for `win-x64`; the browser REPL is a web application and is not part of this MSI. The package is a prototype until its Windows Installer lifecycle acceptance passes.

## Build

From the repository root, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Installer/BuildMsi.ps1 -ProductVersion 0.1.0
```

The script publishes each host, stages the payload under `.local/installer/staging`, builds the MSI, and removes its marked staging directory. The MSI is written to `.local/installer/out/Aloe-lang-<version>-x64.msi`. NuGet restore uses the configured sources by default; `-OfflineRestore` uses the current user's NuGet package cache when all required packages are already cached.

The runtime baseline defaults to `10.0.13`, independently of the installed SDK's default patch. Use `-RuntimeFrameworkVersion <10.0.patch>` to select another .NET 10 servicing patch. Restore and publish use the same version, and the script checks each application's generated runtime configuration before packaging.

The initial version `0.1.0` uses ProductCode `{0FDC5E40-27FB-4891-997D-255CA29F47E6}` and UpgradeCode `{60A2445F-3183-4842-9170-3C9E72A75C93}`. Every later major-upgrade build must specify a higher three-field version and a new ProductCode. WiX generates a unique PackageCode for each build.

`-SkipMsiValidation` is only for build hosts that cannot run Windows Installer ICE validation; it does not satisfy package validation or installation acceptance. Install, upgrade, downgrade, repair, uninstall, environment precedence, ACL, hash, and data-preservation tests must run in a disposable Windows VM.

After building, `powershell -NoProfile -ExecutionPolicy Bypass -File Installer/Test-MsiArtifact.ps1 -MsiPath .local/installer/out/Aloe-lang-0.1.0-x64.msi` decompiles the package and checks identity, architecture, host/config payloads, writable-directory ACL entries, and environment-variable authoring. This static check does not replace ICE validation or VM lifecycle acceptance.

The MSI installs under 64-bit Program Files by default. It preserves a pre-existing machine `ALOE_INSTALL_ROOT`, sets that machine value only when absent or already owned by Aloe, and retains the standard per-user environment override precedence. Standard Users receive Modify rights only under `logs` and `data`; uninstall leaves nonempty user data in place.

## Disposable VM lifecycle runner

`Test-MsiLifecycle.ps1` accepts an initial MSI and a higher-version major-upgrade MSI with a different ProductCode and PackageCode. It checks the Aloe product family, x64/per-machine metadata and package hashes using the Windows Installer database in read-only mode. Its default mode writes a five-step plan and `report.json`; it does not start `msiexec`:

```powershell
pwsh -NoProfile -File Installer/Test-MsiLifecycle.ps1 `
  -InitialMsi C:\VmTest\Aloe-lang-0.1.0-x64.msi `
  -UpgradeMsi C:\VmTest\Aloe-lang-0.1.1-x64.msi `
  -ReportDirectory C:\VmTest\reports-plan
```

For real execution, copy both packages and the script into a dedicated disposable x64 Windows VM and take a clean snapshot. Open an elevated 64-bit PowerShell session in that VM, then add `-Execute -AcknowledgeDisposableVm -DisposableVmName <that-VM-name>`. The name must match `COMPUTERNAME`, and the machine must identify as virtual. The baseline also requires no existing Aloe installation, product-family registration, machine/user environment override, or existing report directory.

The runner installs the initial version, records payload hashes and log/data sentinels, deletes only the installed CLI executable to test full repair, upgrades, checks downgrade rejection against both the exit code and version-error log, and uninstalls. It checks product registration, restored hashes, removed installer-owned files, retained sentinels, and installer-owned machine environment creation/removal. Each step writes a verbose MSI log. A reboot-required result stops as incomplete. On failure the runner preserves the VM state and report for investigation; revert the snapshot before a fresh baseline run.

This runner's baseline success does **not** complete effective standard-user ACL/write acceptance or pre-existing machine/per-user override precedence. Run those scenarios on separate clean VM snapshots and retain their evidence. Build/static checks and mocked runner contracts do not establish any real installation lifecycle result.

### Additional evidence and standard-user probe

The runner now records initial/upgraded SHA-256 maps in `report.json` and checks related-product registration, the Aloe ownership key, and both product uninstall keys in the 64-bit and 32-bit registry views after uninstall. This is targeted installer registration cleanup, not a scan of every registry key or Windows Installer cache entry.

Use a clean disposable-VM snapshot for each run. A root containing user files left by an earlier GUI uninstall is deliberately rejected; do not delete those files merely to satisfy the runner. Preserve earlier reports before reverting a snapshot. Copy the MSI pair and both scripts to a guest-local working directory outside the installation root.

For standard-user acceptance, an account **outside** the Administrators group must be available. Create or select that account yourself in the disposable VM; the scripts do not create accounts or change group membership. Add `-PauseForUserAcceptance` to the elevated lifecycle command. After the initial installation it pauses before repair, while the installed ACLs are available. Switch to that standard account and open a **non-elevated**, 64-bit PowerShell session. Run:

```powershell
powershell -NoProfile -File C:\VmTest\Test-MsiUserAccess.ps1 `
  -Execute -AcknowledgeDisposableVm -DisposableVmName $env:COMPUTERNAME `
  -CheckUserOverride
```

`Test-MsiUserAccess.ps1` defaults to a plan without writes. Execute rejects elevated and filtered-administrator tokens. It creates, reads, modifies, renames and deletes uniquely named probe files in `logs`/`data`, and requires file creation in `bin`/`config` to fail with access denied. It never changes ACLs or existing user files. Its default JSON report is under the standard user's local application-data `AloeMsiValidation` directory; retain the printed path. Return to the administrator session and press Enter to continue the lifecycle. The lifecycle and user reports must be reviewed together; pausing alone does not establish an ACL result.

`-CheckUserOverride` temporarily writes a unique per-user `ALOE_INSTALL_ROOT`, asks Windows `CreateEnvironmentBlock` for a fresh environment without inheriting the shell's stale process environment, checks that the per-user value wins over the existing machine value, and restores the original user value in `finally`. This proves Windows environment composition for that logged-in user, not an Aloe application consuming the environment. Do not sign out or interrupt the process during this temporary check. See Microsoft's [CreateEnvironmentBlock contract](https://learn.microsoft.com/windows/win32/api/userenv/nf-userenv-createenvironmentblock).

For the separate existing-machine-override run, start from another clean snapshot with no Aloe installation or per-user override. Set a test machine value yourself without replacing an existing value, then pass its exact value to the elevated runner:

```powershell
if ($null -ne [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine')) {
  throw 'Use a clean snapshot without a machine override.'
}
$expectedRoot = Join-Path $env:ProgramData 'Aloe-existing-root'
[Environment]::SetEnvironmentVariable('ALOE_INSTALL_ROOT', $expectedRoot, 'Machine')
powershell -NoProfile -File C:\VmTest\Test-MsiLifecycle.ps1 `
  -InitialMsi C:\VmTest\Aloe-lang-0.1.0-x64.msi `
  -UpgradeMsi C:\VmTest\Aloe-lang-0.1.1-x64.msi `
  -ReportDirectory C:\VmTest\reports-existing-override `
  -Execute -AcknowledgeDisposableVm -DisposableVmName $env:COMPUTERNAME `
  -ExpectedMachineOverride $expectedRoot
```

The runner does not create or clear this supplied override. It requires an exact match before installation and verifies preservation after install, repair, upgrade and uninstall. Revert the disposable snapshot after collecting the reports. These scripts execute real installations only when the user supplies `-Execute` and the VM acknowledgement; host plan/parser checks are not VM acceptance.

Command and automation contracts follow Microsoft's [msiexec documentation](https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec), [read-only database modes](https://learn.microsoft.com/windows/win32/msi/installer-opendatabase), and [related-product enumeration](https://learn.microsoft.com/windows/win32/msi/installer-relatedproducts).
