# Windows MSI Scope and Identity

Status: Accepted, 2026-10-19. All-user installation scope selected by the user.

## Context

At decision time, Windows x64 MSI was the project's distribution standard, but no WiX source or lifecycle acceptance existed. The applications share `%INSTALL_ROOT%` with `bin`, `config`, `logs`, and `data`; `ALOE_INSTALL_ROOT` may override the default. Install scope and upgrade identity had to be established before authoring an installer or performing real install tests.

## Decision

- Install for all users as a 64-bit per-machine product. Installation and maintenance require elevation. The default root is the 64-bit Program Files directory under `Aloe`; the MSI may allow a user-selected root.
- Preserve the existing `ALOE_INSTALL_ROOT` override. The MSI may create a machine environment value pointing to its chosen root only when no machine-level value exists; it must not overwrite an administrator's existing value. A per-user environment override retains normal Windows precedence.
- Keep installed binaries and configuration administrator-managed. Grant standard users the minimum write access needed for the `logs` and `data` subdirectories; do not grant write access to the binary directory.
- Preserve `logs` and `data` on uninstall. Remove installer-owned files and empty directories; do not recursively delete user data.
- Use one stable UpgradeCode for the Aloe product family: `{60A2445F-3183-4842-9170-3C9E72A75C93}`.
- Use ProductCode `{0FDC5E40-27FB-4891-997D-255CA29F47E6}` for the initial MSI version `0.1.0`. Every major upgrade increments the three-field Windows Installer ProductVersion and receives a new ProductCode while keeping the UpgradeCode. Downgrades are rejected; minor/small updates are not the distribution path.
- Give each built MSI a unique PackageCode. Preserve component identity across upgrades for unchanged resources, and author components according to Windows Installer component rules.

## Alternatives

- Per-user installation: rejected because the user selected all-user installation and the public distribution target is Windows x64 MSI.
- A per-machine install that replaces an existing machine `ALOE_INSTALL_ROOT`: rejected because it could silently redirect other Aloe hosts or administrator configuration.
- Reusing ProductCode for major upgrades: rejected because major upgrades require a new ProductCode; the stable UpgradeCode identifies the product family.
- Removing the entire root on uninstall: rejected because it would delete logs or user data.

## Impact and security conditions

The installer is elevated and writes only within the selected install root and the Windows Installer product registration/environment entries. Standard users cannot modify installed executables. Writable directories are limited to application logs/data. Existing environment overrides are preserved. Uninstall preserves nonempty logs/data directories.

## Implementation and acceptance

`Installer/Aloe.Installer.wixproj`, `Package.wxs`, and `BuildMsi.ps1` implement the initial `0.1.0` x64 all-user MSI for the CLI, VM host, WASM host, Native REPL, and sample. `Test-MsiArtifact.ps1` decompiles the built MSI and checks package identity, architecture, host/config payloads, debug-symbol exclusion, writable-directory ACL entries, and machine environment-value authoring. These build and static checks do not establish Windows Installer lifecycle acceptance.

Acceptance requires ICE validation and isolated Windows Installer tests for first install, upgrade, downgrade rejection, repair, uninstall, environment-value precedence, directory ACLs, file hashes, and preservation of logs/data. Real system-wide installation tests must use a disposable VM or equivalent isolated Windows environment.

As of 2026-10-21, ICE/static validation and the designated disposable VM's GUI install, missing-file repair, major upgrade, downgrade rejection, uninstall and sentinel-presence checks have passed. These observations do not establish content hashes, installer exit codes/logs, registry cleanup, effective standard-user ACLs or override precedence. `Installer/Test-MsiLifecycle.ps1` collects payload hashes, targeted registration cleanup and optional pre-existing machine-value preservation. `Installer/Test-MsiUserAccess.ps1` separately checks true standard-user access and optional Windows user environment composition; it does not create accounts or change ACLs. Both default to a plan, and their real VM reports are still required for these additional acceptance items.
