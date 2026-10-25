# Local Exact-Version Package Resolution Plan

Status: Accepted and implemented, 2026-10-24. The user accepted this subset in full. API44/44, CLI6/6, complete CompilerLib1836/1836 and all13 Release projects passed. Distribution artifact refresh and real-VM checks are tracked separately in `.local/TODO.md` and `.local/PROGRESS.md`.

## Context

Accepted decision0022 retains the version1 JSON package-location index. `AloePackageDatabase` and `aloec package-db list/add/remove` maintain that index and validate metadata identity, but do not resolve dependency closures. The package specification treats `.meta.json` as declaration/dependency information and the index as a location lookup. Registry trust, signing, range selection and installation are still undefined.

## Accepted first subset

Add a read-only dependency-plan operation using the existing explicit local index:

```text
aloec package-db resolve --name <name> --version <version> [--package-db <path>]
```

- Keep version1 index and metadata schemas unchanged. No additional database provider.
- Package names use the existing exact ordinal, case-sensitive identity. Resolve does not interpret wildcard names.
- In this subset, root versions and metadata `versionRange` values must be an exact three-part numeric version. Each part is `0` or a nonzero ASCII digit followed by ASCII digits. No leading zeroes, signs, whitespace, ranges, wildcards, prerelease or build suffixes. Compare complete version strings for exact identity; do not choose a highest version.
- A reachable dependency must have exactly one matching name/version index entry. Missing roots/dependencies are errors. Existing list/add/remove behavior is unchanged, including their treatment of legacy version strings; resolve rejects versions outside its subset.
- The index supplies file locations. Read the referenced metadata afresh and require its name/version to match the selected index entry. Read dependencies from this metadata, rather than using a potentially stale dependency copy in the index.
- Reuse an already visited identical name/version once. Reject different versions of the same name in one closure. Reject cycles with a dependency path in the diagnostic. Repeated identical dependency edges are collapsed.
- Traverse dependencies in ordinal name/version order and emit each dependency before its dependent; emit the requested root last. This makes the same unchanged inputs produce the same ordered plan.
- Require every selected `.abc` and `.meta.json` file to exist and be readable. Include SHA256 values for the bytes read for both files. Parse and hash metadata from the same byte snapshot. Hashes identify input bytes; they do not establish publisher identity or authorize later execution.
- Registry lookups, HTTP downloads, package copying, index writes, code execution and permanent lock-file writes are outside this operation. File reads follow the explicitly indexed paths and the caller's existing filesystem access.

## Output and failure contract

The library returns a structured plan. The CLI prints one JSON document only after successful resolution, with `formatVersion: 1`, the requested root identity, and a dependency-first package array containing identity, resolved file paths, file hashes and exact dependency identities. No generated timestamp is included in deterministic output.

Invalid JSON/metadata identity, unsupported version syntax, missing dependency/files, conflicting versions, cycles and I/O failures produce a diagnostic and nonzero CLI status. A failure must not emit a partial successful plan or change the index/packages. The original I/O failure remains available to the existing logger.

The plan is a snapshot of files read during this operation. A future build/install command must separately define revalidation and publication behavior; this operation does not promise that external files remain unchanged after it returns.

## Required acceptance

- API and CLI parity for successful resolution and errors.
- Single node, chain, diamond, repeated edge and deterministic dependency ordering.
- Missing root/dependency, metadata/index identity mismatch, unreadable/missing files, invalid metadata and unsupported ranges/versions.
- Direct/indirect cycles and same-name different-version conflicts.
- Case-sensitive names, exact version identities and hash changes after intentional file edits.
- Index/package bytes remain unchanged after both success and failure.
- Existing package-db operations, complete CompilerLib regression and relevant CLI/installer smoke remain passing.

## Deferred decisions

Version-range selection and reconciliation, external registry trust/signing, remote acquisition/cache layout, package installation and source import/linking require later decisions. This proposal makes the first dependency-resolution operation concrete without selecting those policies implicitly.

## Alternatives

- Implement range selection and remote acquisition together: deferred because trust, cache and conflict policies are not yet agreed.
- Continue index-only operations: preserves the current scope but leaves dependency closure unresolved.

## Sources

- `Documents/decisions/0022-package-and-dependency-management.md`
- `Documents/en/Aloe-package-spec.html`, sections1.2,2.2 and3.4.
- `CommonLib/AloePackageDatabase.cs` and `AloeCli/Program.cs`.
