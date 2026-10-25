# Nested List and mixed collection subset

Status: Accepted, 2026-10-14.

## Context

The fixed-length List prototype already reuses array heap fields and Syscalls. Nested arrays have exact child descriptors and counted strong edges. Nested Lists and mixed List/array shapes need to preserve the collection kind at every level.

## Decision

Support nested fixed-length Lists and mixed List/array nesting for the existing supported leaf types. Each suffix is independently typed: `int()()` is List-of-List, `int[]()` is List-of-array, `int()[]` is array-of-List. Empty child literals receive their expected type from context. Indexing, Length, writes, foreach, aliases, and struct snapshot write-back reuse existing collection lowering.

Keep legacy descriptor names for ordinary arrays (`int[][]`) and leaf Lists (`list:int[]`). Encode levels containing nested collections with explicit wrappers `nested:list:<child>` and `nested:array:<child>`, for example `nested:list:list:int[]` and `nested:array:list:int[]`. Each nested element must be a live heap handle with the exact child descriptor. Existing counted strong edges, GC, Tick Committed/Volatile state, and committed-query write guards apply.

No opcode, Syscall ID, heap layout, or AloeBC encoding changes. Older VMs reject the new descriptors. Direct WASM remains unsupported. Fixed length remains; resizing/insertion/removal, Sets/Maps and collection methods remain deferred.

## Alternatives

- Flatten every nested shape into array descriptors: rejected because List/array kind and rank become ambiguous.
- Add separate opcodes or heap layout: rejected because existing generic collection operations already provide the required access and reference semantics.
- Add resizing in this change: deferred because mutable length needs a Tick/Committed-state contract.

## Verification

`Test_CompilerLib/NestedListTests.cs` exercises exact kind distinctions, nested and mixed suffixes, typed empty child context, shared handles, struct field write-back, casts, compile-time mismatch rejection, and raw descriptor access. The full CompilerLib suite passed 394/394, with no failures or skips.
