# Byte Sort Filter Subset

Status: Accepted by the user, 2026-10-28. The byte-sort implementation and local validation passed.

## Context

Accepted sort subsets cover argument-free int/string streams and selected struct keys. The next bounded standard-filter extension can serve existing byte streams without introducing a new comparator or text-collation policy.

A current2087-source CLI probe confirms that an ordinary `pipe<byte>` preserves `255,0,255`, while adding `sort` is rejected with `Filter 'sort' expects pipe<int>, found pipe<byte>.` Evidence is under the internal `byte-sort-probe-20261026/report.json` path. This is a baseline observation, not a failing compiler regression test.

## Decision

- Accept argument-free `sort` with matching `pipe<byte>` input/output, through both existing pipeline filter forms.
- Order unsigned byte values numerically from0 through255 in ascending order. Preserve exact byte values, byte kinds, count and duplicates.
- Buffer input until it closes, then emit the ordered bytes and close the output after draining. Empty input produces empty output. Preserve existing capacity-one scheduling behavior.
- Reject unsupported arguments and incompatible input/output types. Do not silently reinterpret int payloads as bytes or add a new conversion rule for malformed bytecode.
- Keep struct byte-key sorting, descending/custom comparison, seed options and other new sort types outside this increment. Existing int/string and struct-key semantics remain unchanged.
- Apply this to the existing VM filter implementation shared by the supported library targets. Do not add a direct-WASM pipeline backend, new syscall, host permission or public package release.

Example:

```aloe
function main(args: string[]): int {
    let source: pipe<byte> = pipe<byte>.create(1);
    let result: pipe<byte> = pipe<byte>.create(1);
    source |: sort | result;
    let high: byte = 255;
    let low: byte = 0;
    source.write(high);
    source.write(low);
    source.write(high);
    source.close();
    foreach (value in result) { print(value); }
    return 0;
}
```

Expected lines: `0`, `255`, `255`.

## Acceptance

1. Verify empty, singleton, sorted/reverse, duplicate and0/255 boundary inputs, including capacity-one pipelines and both filter forms.
2. Verify output byte kind and exact multiset, serialized bytecode execution, CLI behavior and invalid argument/type rejection. Preserve existing int/string/keyed sort regression coverage.
3. Run both framework test targets and all thirteen Release projects; update bilingual language documentation and affected distribution validation against the new source capture.
4. Keep old artifacts and uncommitted work separate. Passing tests does not authorize a package/tag/release or external delivery.

## Alternatives

- Adopt this bounded byte subset: reuses the existing buffered-sort model and avoids a new comparison policy.
- Defer byte sorting: preserves the current int/string subset while other accepted work proceeds.
