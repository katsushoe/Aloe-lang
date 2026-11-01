# Bool Sort Filter Subset

Status: Accepted by the user, 2026-10-30. Implemented and locally validated: focused17, full2397 per net8/net10 target, all13 Release projects and bilingual filter specification48. The retained2380 CLI probe records the pre-implementation rejection.

## Context

Accepted argument-free sort subsets cover int, string and byte streams. Existing pipe<bool> already preserves boolean values, so extending its sort does not require a new pipe element type. A current2380 CLI baseline successfully prints an ordinary boolean stream in input order and rejects adding sort.

## Decision

- Accept argument-free sort with matching pipe<bool> input and output in both existing pipeline filter forms: `source |: sort | result` and `source | filter(sort) | result`.
- Order false before true. Preserve boolean kind, values, count and duplicates. Do not reinterpret integers or bytes as boolean payloads.
- Buffer input until it closes, then emit ordered values and close output after draining. Empty input produces empty output. Retain existing cooperative scheduling and capacity-one backpressure behavior.
- Reject arguments, incompatible pipe types and nonboolean runtime payloads in malformed bytecode. Ordinary source type checks remain in force.
- Keep keyed struct sorting by bool fields, descending/custom comparison, other new element types and a direct-WASM pipeline backend outside this increment. Existing accepted sorts retain their contracts.

Example:

```aloe
function main(args: string[]): int {
    let source: pipe<bool> = pipe<bool>.create(1);
    let result: pipe<bool> = pipe<bool>.create(1);
    source |: sort | result;
    source.write(true); source.write(false); source.write(true); source.close();
    foreach(value in result) { let item: bool = value; print(item); }
    return 0;
}
```

Expected boolean sequence: false, true, true.

## Implementation and acceptance

Implement compiler binding/type checks and VM boolean buffering/output using the existing sort scheduling model. No new source conversion or bytecode section is proposed. A new built-in filter binding must be recognized and validated by the current VM; older unsupported consumers must not silently treat it as another sort type.

Verify empty/singleton/ordered/reverse/duplicate cases, exact boolean kinds, both filter forms, capacity-one pipelines and serialized bytecode. Verify invalid arguments, incompatible pipe types, nonboolean payloads and closed output. Preserve int/string/byte/keyed-sort coverage; run both framework test targets and all13 Release projects. Update English and Japanese standard-filter documentation in the same accepted implementation.

Existing frozen MSI artifacts remain separate; this proposal does not request a per-feature MSI/ISO rebuild, package release or external delivery.

## Security and operational conditions

Memory grows with input count under the existing full-stream buffering contract. This is not an untrusted-code isolation or resource-limit guarantee. The change adds no filesystem, network, permission, registry or host-syscall capability.

## Alternatives

- Adopt the bounded boolean subset: reuses an existing pipe type and gives a single explicit order.
- Defer boolean sort: retain the currently accepted int/string/byte subsets while other work proceeds.

Keyed bool records, general comparators and additional pipe element types require separate contracts.
