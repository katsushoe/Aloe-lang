# .NET 8 Library Compatibility Targets

Status: Accepted, 2026-10-25. The user adopted this proposal in full. Product source, CI and build requirements have now been updated. Integrated Release validation passes for all thirteen projects and all 1868 tests on each target. A fresh net8.0-windows consumer passes eleven checks against the integrated assemblies. Refreshed MSI static/extracted-host checks, ISO round-trip and Browser static/HTTP checks pass; live Browser UI, disposable-VM lifecycle and immutable consumer delivery remain pending. The earlier isolated experiment acceptance does not replace this validation.

## Context

Neon requires an in-process compiler and VM dependency for `net8.0-windows`. Before this decision, Aloe declared `net10.0` throughout its libraries and applications. The earlier accepted desktop/browser capture remains the .NET 10.0.13 build with 1868 compiler tests; it does not certify the newly integrated source.

An isolated source candidate builds five libraries and the existing test project for `net8.0`: CommonLib, CompilerLib, RuntimeLib, Aloe2WasmLib and AloeRepl.Core. Its Release build has zero warnings/errors and all 1868 tests pass without skips. Two library calls and three test calls to `Convert.ToHexStringLower` are expressed as `Convert.ToHexString(...).ToLowerInvariant()` in that candidate. At the time of that isolated experiment, the original product sources and target frameworks remained unchanged; the accepted integration described above was applied afterward. A fresh `net8.0-windows` consumer restores, builds without warnings/errors and passes eleven assertions on .NET 8.0.31. This uses isolated source references, not a published package or integration into Neon.

## Decision

- Add `net8.0;net10.0` targets to the five libraries above and the common test project. Including the two auxiliary libraries permits the complete existing test suite to run for both targets without maintaining a second test fork.
- Keep all desktop hosts, Browser REPL, MSI payload and their servicing version on .NET 10. Do not change Neon's target framework from this repository.
- Use the compatible hex conversion in the two library and three test locations. Preserve lowercase SHA256 output and the existing package resolver contract.
- Give the six multi-target projects framework-specific build/intermediate destinations. The current shared install-root output must not allow the two framework builds to overwrite the same assembly or generated reference file. Preserve ordinary application deployment paths and publish layouts.
- Retain the existing .NET 10 SDK workflow and central package versions. Regenerate and verify framework-specific lock graphs. Make the .NET 8 runtime available to the CI test job, then perform locked restore, warning-free builds and the complete suite for both targets.
- Treat the supported language, VM, bytecode and host capability subset as unchanged. Compatibility does not imply complete Aloe specification coverage.
- Select an immutable integrated source revision and verify a clean consumer before describing the dependency as delivered to Neon. Dirty working-tree hashes, isolated candidate directories and the current documentation-oriented HEAD are not a released dependency.

NuGet package IDs, package version allocation, signing, registry publication and delivery credentials remain separate decisions. This proposal authorizes target/build/test compatibility only; release and external publication still require explicit authorization.

## Support and security conditions

Microsoft states that .NET 8 support ends on November 10, 2026. This compatibility bridge does not extend Microsoft's servicing or promise indefinite security maintenance for a .NET 8 host. Review the bridge at that boundary with the consuming project; do not silently migrate Neon or suppress its requirements.

No new filesystem, network, syscall or execution permission is introduced. Consumers retain the existing host capability and trust-boundary responsibilities. Preserve the current .NET 10 artifacts until newly integrated outputs pass their own acceptance checks.

## Alternatives

- Keep .NET 10 libraries only: smallest maintenance scope, but does not meet the reported in-process .NET 8 consumer requirement.
- Add only the three core library targets: smaller public compatibility surface, but the current combined test project also references the WASM generator and REPL core; a separate test organization would be required for complete dual-target coverage.
- Use a separate .NET 10 process: requires a new IPC, lifecycle and capability contract; no such supported contract has been accepted.
- Ship the rewritten experiment as a separate fork: useful for investigation, but creates source drift and is not a maintainable release contract.

## Required acceptance and documentation

1. Verify clean locked restore and Release build for both library targets, with no warnings/errors or shared-output collisions.
2. Run all 1868 current cases for both targets, including package hashes, compiler diagnostics, VM lifecycle, REPL and direct WASM behavior.
3. Build and execute a fresh `net8.0-windows` consumer against the three core libraries. Keep this distinct from integration into Neon itself.
4. Rebuild all thirteen ordinary Release projects and recertify affected .NET 10 desktop, MSI and Browser outputs after product changes. Do not reuse the old captured artifact acceptance for newly built bytes.
5. Update public dependency/build requirements, CI and lock files together; describe the precise supported subset and support boundary.
6. Select and validate an immutable source/package identity through the authorized provider route before consumer delivery. No release is implicit in passing tests.

## Evidence and sources

- Isolated candidate evidence: `net8-candidate-20261024/full-regression-20261025.log`, `test-results/net8-candidate-full-20261025.trx` and `runtime-report.json` under the internal evidence root.
- Existing target declarations in CommonLib, CompilerLib, RuntimeLib, Aloe2WasmLib, AloeRepl.Core and Test_CompilerLib; shared output rules in `Directory.Build.props`.
- [Microsoft .NET 8 and .NET 9 end-of-support notice](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/).
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).
