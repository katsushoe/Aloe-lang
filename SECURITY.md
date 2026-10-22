# Aloe Security

[English](SECURITY.md) | [日本語](SECURITY.ja.md)

Last reviewed: 2026-10-22. This document describes the current reference implementation, its trust boundaries, and how to report security concerns. It does not certify that Aloe is secure or constitute an independent security audit.

## Status and supported versions

Aloe is experimental and is not intended for production use. Language and VM specifications include design targets that are not all implemented. Evaluate the compiler, runtime, host, configuration, and dependencies together; a specification requirement is not evidence of an implemented protection.

No long-term security-support period, backport policy, or response deadline is promised here. Include the affected version or commit in reports and, where safe, check whether the issue also affects the current development revision. Older builds must not be assumed safe merely because they still run.

## Threat model and trust boundaries

| Input or component | Trust boundary and required handling |
| :--- | :--- |
| Aloe source and serialized AloeBC | Treat third-party programs as untrusted, including during parsing, compilation, and loading. The current VM is not an established isolation boundary for hostile programs. |
| Embedding host and callbacks | The host is trusted and executes with its process permissions. Review callbacks and registered syscalls before exposing them to programs. |
| Configuration and dependencies | Configuration can grant capabilities and select data/log locations. Protect it and the toolchain from untrusted modification; restore dependencies from trusted sources. |
| Program input, output, errors, and logs | They may contain attacker-controlled text or sensitive data. Limit their size and access, and encode data for its eventual output context. |

Static typing, heap handles, GC, and Tick rules are useful language/runtime mechanisms. They do not replace OS isolation, resource limits, or application authorization.

## Implemented controls and their limits

| Control | Current behavior and limit |
| :--- | :--- |
| Load-time bytecode checks | `AloeVm` calls `AloeBytecodeVerifier` before execution. It checks structural properties such as opcodes, indices, and control structure. Full type-stack validation is deferred until function type metadata is available. The broader verifier requirements in the VM specification are design requirements, not a claim that all are implemented. |
| Runtime value checks | Collection operations check applicable handles, indices, descriptors, value kinds, and ranges. These checks do not authenticate arbitrary host-created objects or establish complete type safety for arbitrary raw bytecode. |
| Host capability checks | `HostSyscallRegistry` checks required capabilities before invoking registered handlers. The current flags are `None`, `ConsoleInput`, `Timer`, and `All` (`ConsoleInput | Timer`). `All` is the default, not an OS permission grant. |
| Browser REPL restrictions | The browser REPL overrides host capabilities to `None`; `readLine()` and `sleep(ms)` are rejected. This does not bound CPU or memory use, or make all compiled WASM hosts equivalent. |

Implementation references: [verifier](RuntimeLib/AloeBytecodeVerifier.cs), [VM settings](RuntimeLib/AloeVmSettings.cs), [capability flags](RuntimeLib/AloeHostCapability.cs), [host syscall registry](RuntimeLib/HostSyscallRegistry.cs), and [browser REPL decision](Documents/decisions/0019-browser-repl-host-capabilities.md).

## Known limitations

- Full type-stack verification of arbitrary AloeBC is not implemented. Passing structural verification is not a certificate that a program is safe.
- VM settings do not currently provide a total execution-time or total-memory quota. Instruction scheduling quanta, GC work budgets, `MaxVmThreads`, and cooperative `RequestHalt()` are not hard resource-isolation guarantees. Blocking host callbacks can also prevent timely progress.
- Some filters retain their complete input before producing output. Bounded pipe capacity does not bound the total memory used by such aggregation.
- Runtime type descriptors and raw heap APIs rely on caller contracts in places; a compiler-generated descriptor is not authentication against a malicious host or arbitrary bytecode producer.
- The reference VM's checks do not automatically establish equivalent guarantees for direct WASM output. The WASM engine, imports, callbacks, and execution limits belong to the embedding host's security boundary.
- No claim of a completed independent security audit, comprehensive fuzzing, or resistance to sandbox escape is made here.

## Safer execution and configuration

For local experiments, use programs and toolchains you trust. If an application must accept untrusted programs, isolate parsing, compilation, loading, and execution in a disposable, low-privilege process or environment. Apply host-enforced time, CPU, memory, output, and process-count limits. Keep credentials and sensitive files out of that environment, and restrict network and filesystem access at the OS/container boundary. These are deployment requirements for the embedding application, not built-in Aloe features.

Explicitly grant only the host capabilities needed. For example, when constructing the reference VM from an already compiled `module`:

```csharp
var settings = new Aloe.RuntimeLib.AloeVmSettings
{
    AllowedHostCapabilities = Aloe.RuntimeLib.AloeHostCapability.None
};
var vm = new Aloe.RuntimeLib.AloeVm(module, settings);
```

For a host that loads `aloevm.json` through `AloeVmSettings.Load()`, the capability setting can be expressed as:

```json
{
  "allowedHostCapabilities": "None"
}
```

Merge this setting into the actual configuration file used by that host. The default resolver checks the installed configuration directory, then `config` under the current directory, the current directory itself, and the application directory; an explicitly supplied rooted file path is handled separately. An absent configuration or omitted capability setting defaults to `All`. Check configuration selection and permissions rather than assuming a nearby file was loaded.

`None` denies the currently registered capability-gated input/timer operations. It does not disable all output, logging, internal VM operations, or arbitrary behavior in custom host code. Review logging destinations, trace settings, callbacks, and data directories separately; do not pass secrets into programs or publish unredacted logs.

## Data transformation and cryptography

- `shuffle` uses noncryptographic randomness. Do not use it to create passwords, keys, or security tokens.
- `sha1` and `md4` exist for legacy compatibility and must not be used for security decisions. A plain `sha256` digest does not by itself authenticate data or implement password storage.
- `strip_tags` is not an HTML sanitizer. Escaping, entity decoding, URL encoding, and CSV quoting do not replace context-appropriate validation or protection at the consuming application boundary.

See the [standard filter specification](Documents/en/Aloe-standard-filters.html) for individual contracts and buffering behavior.

## Dependencies and security verification

Follow [PACKAGES.md](PACKAGES.md) for exact dependency versions, lock files, update review, and audit handling. The existing dependency-check entry point is:

```powershell
./scripts/Test-Dependencies.ps1
```

It performs locked restores and online dependency audits for the solution and WiX. An unavailable audit source is an unverified result, not a clean security result. Dependency audits do not test the Aloe language or runtime for logic vulnerabilities.

When changing a security boundary, verify both successful and rejected operations, including whether denial occurs before host side effects. Relevant tests include [host syscall tests](Test_CompilerLib/HostSyscallTests.cs) and [REPL session tests](Test_CompilerLib/AloeReplSessionTests.cs). Record the tested revision, configuration, and scope; passing functional tests alone is not a security audit.

## Reporting a vulnerability

Do not disclose exploit details, credentials, personal data, or affected private source in public issues or pull requests.

1. In the repository's GitHub **Security** tab, use **Report a vulnerability** if that private reporting option is available. Its availability is not asserted by this document.
2. If private reporting is unavailable and you have no agreed private maintainer contact, open only a minimal issue requesting a private reporting channel. Do not include the payload, reproduction steps, or sensitive attachments. Wait until a private channel is agreed before sharing those details. No dedicated security email address is designated here.
3. In that private channel, provide the affected version/commit, component and host, OS/runtime, relevant capability settings, expected and observed behavior, impact, and a minimal reproduction using synthetic data. Explain any necessary privileges. Redact secrets from logs and attachments.
4. Test only on systems you own or are authorized to test. Coordinate disclosure of actionable details with the maintainer.

Maintainer handling should identify the affected trust boundary, reproduce the issue safely, assess affected revisions and severity, agree on remediation and disclosure, and verify a fix with regression coverage. Publish mitigation or advisory information when appropriate without exposing reporters' sensitive data. No acknowledgement or remediation deadline is guaranteed.

## Related documentation

- [VM specification](Documents/en/Aloe-vm-spec.html): intended verification and execution contracts; consult the implementation limits above.
- [Package management decision](Documents/decisions/0022-package-and-dependency-management.md): dependency trust and audit limits.
- [Document index](DOCUMENTS.md): other public documentation.
