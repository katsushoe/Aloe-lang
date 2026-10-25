# Package inventory

[English](PACKAGES.md) | [日本語](PACKAGES.ja.md)

This file records direct NuGet package references. Project-to-project references are source dependencies and are not repeated here. `Directory.Packages.props` owns exact package versions; project files own the references and their metadata. On 2026-10-20, solution and separate WiX locked restores and online dependency audits passed without changing resolved package versions.

## Direct package references

| Package | Version | Referencing project | Purpose / metadata |
| :--- | :--- | :--- | :--- |
| `Microsoft.AspNetCore.Components.WebAssembly` | `10.0.13` | `AloeRepl.Wasm` | Blazor WebAssembly runtime and build integration. |
| `Microsoft.AspNetCore.Components.WebAssembly.DevServer` | `10.0.13` | `AloeRepl.Wasm` | Local development server; `PrivateAssets="all"`. |
| `coverlet.collector` | `6.0.4` | `Test_CompilerLib` | Test coverage collector; private to the test project. |
| `Microsoft.NET.Test.Sdk` | `18.0.1` | `Test_CompilerLib` | .NET test execution infrastructure. |
| `NUnit` | `4.4.0` | `Test_CompilerLib` | Unit test framework. |
| `NUnit.Analyzers` | `4.11.2` | `Test_CompilerLib` | NUnit diagnostics; private to the test project. |
| `NUnit3TestAdapter` | `5.2.0` | `Test_CompilerLib` | VSTest adapter for NUnit tests. |
| `WixToolset.Util.wixext` | `7.0.0` | `Installer/Aloe.Installer.wixproj` | WiX utility extension; validate rebuilt MSI artifacts after updates. |

## Maintenance

- Update this inventory in the same change as any direct `PackageReference` addition, removal, version change, or metadata change.
- Project references such as `ProjectReference` are tracked by the `.csproj` files and are not third-party packages.
- Package restore sources and credentials are supplied by the active NuGet configuration; this file does not store credentials or machine-specific source settings.
- Follow accepted [decision 0022](Documents/decisions/0022-package-and-dependency-management.md): exact central versions, no transitive pinning, and version-controlled `packages.lock.json` files for the test, Browser REPL, and installer projects. The WiX SDK version remains in its project declaration.
- Run `./scripts/Test-Dependencies.ps1` for CI/release dependency validation. It checks locked restores and online audits for both the solution and WiX and saves JSON reports under `.local/dependency-audit`. Audit-source failures and high/critical findings fail validation; moderate findings require a recorded disposition before release. Low findings are reviewed during maintenance.
- Review updates during .NET servicing and at least quarterly. Update central versions and lock files together, then build, test, and validate affected hosts/installers. CI provider integration and automatic update services remain deferred until ownership is selected.
- The Browser project declares `RuntimeFrameworkVersion=10.0.13` so ordinary locked restore and release publish select the same SDK-managed dependencies. Its internal ASP.NET assets, ILLink tasks, and WebAssembly pack lock at 10.0.13; installed Emscripten workload tools are separate.

