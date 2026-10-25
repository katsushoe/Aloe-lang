# Basic Map Subset

Status: Accepted, 2026-10-14.

## Context

The language specification shows both `map<T>` and `map<K/V>` and bracketed key/value literals. It does not yet define their default key type or runtime operations. The user approved treating `map<T>` as a string-keyed Map and `map<K/V>` as the explicit form.

## Decision

Canonicalize `map<T>` to `map<string/T>`. Explicit `map<K/V>` permits primitive scalar or normal/bitfield enum keys and values; keys and values additionally permit statically named class references, values permit generic object references and named-box references. Class-reference keys compare by ObjectId identity; generic object, struct and collection keys remain unsupported. Struct values and collection values remain unsupported. Use `[key: value, ...]` literals, including `[]` in an expected Map context. Nonempty literals infer key and value types; keys and values evaluate once in source order. String keys compare with ordinal, case-sensitive equality; other keys use the existing exact-kind Set equality (including nominal enum/int runtime-kind separation enforced by the compiler). Duplicate keys use the last value while retaining their first insertion position.

Expose read-only `Count`, indexed reads, `ContainsKey(key)`, `Set(key, value)`, and `Remove(key)`. A missing indexed key raises VmException; removing a missing key is a no-op. Set replaces an existing value or appends a new key. Mutation statements initially require a Map local or parameter. `foreach(key in map)` visits keys in insertion order; `foreach(key, value in map)` assigns both locals at the start of each iteration, with value read from the Map for that key. Mutations in the loop body affect later iterations but not the current locals. Indexed assignment, struct values, nested Map types and direct WASM are deferred.

Maps share one heap ObjectId across aliases and function/class/async boundaries. Heap fields alternate key and value. Runtime descriptors are `map:<key-array-descriptor>|<value-array-descriptor>`; descriptor splitting validates candidates because class key and value descriptors can each contain `|`. Syscalls 41-47 create/read/mutate/query Maps and enumerate keys without changing opcode or AloeBC encoding. Class-reference keys retain their objects through existing strong heap edges. Normal execution reads Volatile Tick state and property queries read Committed state; all mutations during committed property queries fail.

## Alternatives

- Require only `map<K/V>`: rejected because existing examples use the approved shorthand.
- Generic object-reference keys: deferred because their erased runtime kind does not preserve a statically named class contract. Class-reference keys use defined ObjectId identity equality.
- Add a separate dictionary outside the heap: rejected because it would duplicate identity and Tick snapshot management.
- Return a default value for missing keys: rejected because it would hide a missing key; callers can use ContainsKey.

## Impact and conditions

Heap resizing accepts Map descriptors while preserving identity and snapshots. Runtime calls validate descriptors, key kinds, value kinds/ranges, even field counts and live handles. This is a linear lookup prototype, without a hashing/performance guarantee. No host permissions or external configuration are added. Older VMs cannot execute the new Syscalls. Tests and bilingual language/VM documentation must cover aliases, inference and empty contexts, exact type checks, duplicates, evaluation order, Tick/query behavior, GC, missing keys and malformed calls.

## Interface reference extension

The Map subset also supports interface references. The collection type remains invariant, while elements or keys/values may use conforming class instances. The descriptor and dispatch rules are defined in [the interface decision](0018-interface-async-dispatch-subset.md); existing identity, heap and Tick rules apply.
