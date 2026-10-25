# .NET 10 Target Framework

Status: Accepted, 2026-10-19. The user selected .NET 10 LTS for Aloe-lang.

## Context

All source and test projects targeted `net8.0`. Microsoft's support policy lists .NET 8 support ending on 2026-11-10 and .NET 10 LTS support ending on 2028-11-14. At the decision date, the latest .NET 10 patch is 10.0.13. An isolated migration probe compiled all non-browser projects, passed compiler tests, and published the five Windows hosts, but Browser REPL and latest-patch release checks remained incomplete.

## Decision

- Target `net10.0` in every source and test project, including Browser REPL.
- Align Browser REPL's `Microsoft.AspNetCore.Components.WebAssembly` and `DevServer` package references to 10.0.13, the current patch at decision time.
- Treat the current .NET 10 patch as the release validation baseline and refresh the version when Microsoft ships servicing updates.
- Keep the Windows MSI's existing five-host scope and installation identity. Rebuild and statically validate the MSI after the migration; do not use this framework decision as authorization to install or release it.

## Alternatives

- Retain .NET 8 and maintain a separate compatibility stream: not selected because the user chose the supported .NET 10 LTS line.
- Multi-target .NET 8 and .NET 10: not selected because no compatibility requirement was identified and it would double the build and test matrix.

## Impact and security conditions

All consumers must have .NET 10 when running framework-dependent builds. Self-contained Windows packages continue to include their runtime. The migration must not suppress compiler or analyzer warnings. Release artifacts must use the latest supported .NET 10 patch and pass the repository's host and installer checks.

## Implementation and acceptance

Update all project TFMs, Browser REPL package references, package inventory, and host instructions together. Restore and build the full solution, run the compiler test suite, publish and smoke-run all five Windows hosts, and rebuild the all-user MSI with its static artifact checks. Browser REPL restore/build/publish must be included. MSI installation lifecycle remains subject to the separate disposable-VM acceptance requirements in decision 0020.
