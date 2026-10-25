# Package and Dependency Management

Status: Accepted, 2026-10-20. The user accepted the proposal in full.

## Context

Aloe-lang has 13 .NET source/test projects, a separate WiX installer project, and a small set of direct NuGet references. Before this decision, package versions were project-local and there was no repository lock-file policy. The current direct and resolved package inventory is recorded in `PACKAGES.md` and `.local/PACKAGES.md`.

The Aloe package specification defines `<package>.abc`, `<package>.meta.json`, and a JSON `package-db.json` index. That package index is not a relational database. No SQL/EF Core provider or database connection API is currently used by the source projects. A separate compiler/runtime persistence requirement has not been identified.

## Decision

- Use NuGet Central Package Management in a root `Directory.Packages.props` for direct package versions shared or owned by this repository. Keep `PackageReference` entries in the projects that use them, and keep the WiX SDK declaration visible in its project file.
- Use exact, non-floating versions. Do not enable transitive package pinning; update a transitive dependency only through an explicit direct reference with a documented reason.
- Commit a `packages.lock.json` for each project that has NuGet `PackageReference` dependencies, including test and installer projects. Generate or update lock files during intentional dependency changes, and require locked-mode restore in CI/release validation.
- Audit direct and transitive NuGet dependencies in connected CI using NuGet Audit. High and critical findings block release; moderate findings require a recorded disposition before release; low findings are reviewed during dependency maintenance. Network/audit-source failures must remain visible and cannot count as a passed release audit. Offline local builds may opt out only for that local invocation, not by changing repository policy.
- Do not add SQL Server, SQLite, EF Core, or another relational database package to the compiler/runtime today. Retain the specified JSON `package-db.json` as the Aloe package-location index and `.meta.json` as package metadata. Revisit a database engine only when a concrete query, concurrency, mutability, or synchronization requirement exceeds the file-based model.
- Use GitHub Actions for push and pull-request validation. Locked restore, full Release solution build, CompilerLib tests and the provider-independent solution/WiX dependency audit run as CI checks; there is no publishing step. Keep automatic dependency-update services deferred. Review package updates during .NET servicing updates and at least quarterly; each update must include restore, build, tests, and relevant host/installer validation.

## Alternatives

- Keep versions duplicated in project files: simple today, but makes aligned updates across projects easier to miss.
- Enable transitive pinning: not selected because it can promote transitive dependencies into package metadata and change downstream dependency behavior.
- Add a relational database preemptively: not selected because the current compiler has no relational persistence use case, and a database/provider would add deployment, compatibility, and maintenance cost.
- Add an update bot immediately: deferred; repository CI is now configured, but automated update review ownership and update cadence automation remain outside this decision.

## Impact and security conditions

Central versions reduce drift but do not by themselves guarantee reproducible restores; lock files and locked-mode CI provide that control. Vulnerability audit results depend on an available trusted audit source. A failed audit lookup is an unverified result, not a clean bill of health. This decision does not define package-registry trust, package signing, cache layout, or the full Aloe package-resolution algorithm; those require separate decisions before external package installation is implemented.

## Implementation and acceptance

Implemented on 2026-10-20: `Directory.Packages.props` owns eight exact direct versions, and the three package-consuming projects have versionless references and lock files. Resolved package identities and versions were unchanged (test: 16, Browser REPL: 31, installer: 1). The WiX SDK declaration remains local to its project. On 2026-10-21, `.github/workflows/ci.yml` connected the audit script to GitHub Actions push and pull-request checks; the local approved host-network run passed solution and WiX locked restore and vulnerability inventories.

Subsequent release validation found a Browser mismatch: ordinary restore selected SDK packages at 10.0.12 while the existing publish command requested RuntimeFrameworkVersion=10.0.13, correctly causing NU1004 in locked mode. The Browser project now declares 10.0.13 consistently with accepted decision 0021; its three SDK-selected packages (ASP.NET internal assets, ILLink tasks, and WebAssembly pack) intentionally update to 10.0.13. The other two locks and all eight centrally owned direct versions remain unchanged. Keep build and publish runtime properties aligned when refreshing locks.

`scripts/Test-Dependencies.ps1` is the provider-independent CI/release entry point. It performs locked restores and online audits for the solution and WiX separately and preserves JSON reports. CI properties reject audit-source errors and high/critical findings; the script also checks reported severities and reports moderate findings for disposition. The 2026-10-21 solution and WiX audit run passed with no blocking vulnerability entries. The GitHub Actions workflow does not publish artifacts or releases.

After intentional dependency updates, run the existing build/test/host and MSI static acceptance checks for affected components. The package index continues to use JSON; external Aloe package metadata, resolution, registry trust, and signing remain separate implementation work.

## References

- [NuGet Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
- [NuGet lock files and locked restore](https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files#locking-dependencies)
- [NuGet dependency auditing](https://learn.microsoft.com/nuget/concepts/auditing-packages)
- [EF Core database providers](https://learn.microsoft.com/ef/core/providers/)
