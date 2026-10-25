# Aloe Programming Language

[English](ReadMe.md) | [日本語](ReadMe-ja.md)


> A small, strongly-typed, “pipe-oriented” language being designed together with a generative AI.
> License: MIT


Aloe is an **experimental programming language** with static typing, a simple syntax,
and a stack-based VM. It is especially designed to treat **I/O and data transformation**
via **pipes** and **filters** as first-class features.


Current focus:


- A **language specification** that is readable and explicitly written down
- An AloeVM bytecode format (`AloeBC`) that is **easy to map to WebAssembly**
- A **pipe/filter model** that feels good for text processing, REST calls, and streaming
- A **race-free multithreaded execution model** based on Object ownership, CallBuffer, Volatile→Committed State, and Tick


> ⚠️ Aloe is experimental and not intended for production use.
> The syntax, VM, and toolchain may still change in backward-incompatible ways.


---


## Documentation


- Aloe language specification (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/en/Aloe-lang-spec.html
- AloeVM specification (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/en/Aloe-vm-spec.html
- AloeVM opcode specification (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/en/Aloe-vm-opcode.html
- Aloe standard filter library (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/en/Aloe-standard-filters.html
- Aloe standard library specification (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/en/Aloe-stdlib-spec.html
- Aloe package specification (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/en/Aloe-package-spec.html
- Repository document index: [DOCUMENTS.md](DOCUMENTS.md)
- Direct NuGet package inventory: [PACKAGES.md](PACKAGES.md)
- Security boundaries, limitations, and vulnerability reporting: [SECURITY.md](SECURITY.md)


---


## Features (Design Goals)


### Static Typing + Type Inference


```aloe
var n = 42;      // inferred as int
var pi = 3.14;   // inferred as float
let name: string = "Aloe";
```


- Local type inference with `var`
- Explicit type annotations with `let`


### Clear Distinction Between Value Types and Reference Types


- `struct` … value type (copied by value)
- `class` … reference type (heap-allocated)
- `delete obj;` … dedicated syntax that sets the reference to `null`
  (`obj = null;` is not allowed in source code)


### Simple, Explicit Control Structures


- `;` is required at the end of every statement
- Indentation has no meaning (unlike Python-style syntax)
- Synchronous catch-all and ordered type-only `try / catch` clauses for existing exception classes, plus `throw;` rethrow, are implemented; exception values, `finally`, and async exception handling remain unsupported.


---


## I/O and Data Flow with Pipes and Filters


You can describe data pipelines at the language level.


```aloe
function main(args: string[]): int {
    let lines: pipe<string> = pipe<string>.create();


    stdin
      | filter(utf8)       // byte -> string
      | filter(lineSplit)  // string -> string (one line each)
      | lines;


    foreach (line in lines) {
        if (line == "") { break; }
        print("LINE: " + line);
    }


    return 0; // exit code
}
```


### Defining a Filter


```aloe
filter lineSplit {
    in:  pipe<string>;
    out: pipe<string>;


    bound(input, output) {
        foreach (chunk in input) {
            let lines = chunk.split("\n");
            foreach (line in lines) {
                output.write(line);
            }
        }
        output.close();
    }
}
```


- Input/output types are declared with `in` / `out`
- `bound(input, output)` implements the actual transformation
- In a pipeline you use it as `filter(lineSplit)`


---


## Proposed Mix-in Style with `trait` + `with`

The following is a design sketch, not executable Aloe code: the current compiler does not implement trait declarations or applying traits with `with`.


```aloe
trait Printable {
    method printSelf(): void {
        print(this.toString());
    }
}


class User {
    private name: string;


    construct(name: string) {
        this.name = name;
    }


    public property Name: string {
        get { return name; }
    }


    private method toString(): string {
        return "User(" + name + ")";
    }
}


function main(args: string[]): int {
    var u = new User("alice");


    var p = u with Printable;
    p.printSelf();   // "User(alice)"


    return 0;
}
```


- A trait is intended to be a reusable unit that can define methods.
- Applying traits to instances with `with` remains planned; ordinary `with` blocks have separate supported behavior.


---


## Overview of AloeVM & AloeBC


- Assumes a stack-based VM
- Compact bytecode format `AloeBC`
  - Starts with the magic `"ALOEBC"` and version bytes (e.g. Major/Minor/Build)
- Instruction set is designed to map **as closely 1:1 as possible to WebAssembly**


## Object / Tick / CallBuffer VM (Design Draft)


Aloe's parallel execution model uses the **class Object as the ownership unit for Mutable State**.
Another Object does not directly read or write class member variables: state is read through a `public property`, and state-change requests use a `public async method`.


Core principles:


- Only an Object itself modifies its member variables
- A `public property` is a synchronous Query that exposes Committed State
- There is no public property setter
- A public instance method callable from another Object is `public async method ... : void`
- `async` does not mean C# async/await; it means **enqueue in the target Object's CallBuffer**
- A self call (`targetObject == currentObject`) executes synchronously
- Private / protected methods, ordinary functions, and ordinary static methods may execute synchronously
- A `public static async method` uses a per-type **Static CallBuffer**
- Internal Object updates during a Tick are **Volatile State**
- Other Objects see only **Committed State** until Tick end
- Tick end performs `Volatile -> Committed`
- The previous `TaskChangeBuffer` / Change Record model is removed
- Calls enqueued while a Tick is running are also processed in that Tick
- **Tick Quiescence** requires empty Instance / Static CallBuffers, zero running async methods, and zero Scheduler pending work
- `tick()` is a synchronous Barrier driven from main
- VM Tick is separate from Render Frame / VSYNC
- CallBuffer execution is race-free, but complete Replay Determinism is not currently guaranteed because lock acquisition order may depend on OS scheduling


### State visibility


```text
Committed hp = 100
Volatile hp  = 70


Owner Object:
    hp -> 70


Another Object:
    player.HP -> 100
```


### CallBuffer


```text
Caller
   |
   v
Target Object CallBuffer
   |
   v
Owner Object async method
   |
   v
Volatile State
   |
   v
Tick Commit
```


### Tick


```text
main()
  |
  | Root / Tick Driver
  v
tick()
  |
  +-- Instance CallBuffer execution
  +-- Static CallBuffer execution
  +-- newly generated Calls
  |
  v
Tick Quiescence
  |
  v
Volatile -> Committed
  |
  v
Reference Graph / Reference Group
  |
  v
Reference Count / GC
  |
  v
return to main()
```


Object Reference lifetime uses **Reference Counting** as the base mechanism. Cycles are represented by SCCs in the Object Reference Graph called **Reference Groups**.
An Object / Group whose Reference Count reaches 0 is first marked as a Destruction Candidate; Tick-end processing destroys it only after considering Runtime Temporary References such as CallBuffer entries, VM-stack references, and Volatile References.


---
### WASM-Friendly Design


- AloeVM instructions are designed so that they can be mapped straightforwardly to WebAssembly opcodes
- Long-term ideal:


  - Development flow:
    `Aloe source → WASM → Execution environment (browser / server / etc.)`
  - The `AloeVM` interpreter is mainly for debugging and non-WASM environments


---


## Example: Simple REST-Like Pipeline


This is an imagined example that fetches one user record using an HTTP client
and a JSON filter (the concrete standard library APIs are still under design).


```aloe
struct UserInfo {
    field id:    int;
    field name:  string;
    field email: string;
}


function main(args: string[]): int {
    let request:  pipe<byte>     = pipe<byte>.create();
    let response: pipe<byte>     = pipe<byte>.create();
    let users:    pipe<UserInfo> = pipe<UserInfo>.create();


    // Pseudo code: httpClient performs an HTTP call using request/response
    httpClient("GET", "https://api.example.com/users/1", request, response);


    response
        | filter(utf8)
        | filter(json<UserInfo>)
        | users;


    // Imaginary method that pulls the first element from the current pipe
    let u = users.next();


    if (u is UserInfo) {
        print("User: " + u.name + " <" + u.email + ">");
    }


    return 0;
}
```


Note: details such as `httpClient`, options for `json<T>`, and most standard filter/library
APIs are being designed in separate documents. The VM implements `json: T` for declared structs with required, exact-name `int`, `bool`, and `string` fields (int requires lexical JSON integer syntax, with no fraction or exponent notation); malformed JSON or mismatches fail with the input item and optional member in the error. Use `source |: json: T | destination;`. The VM also includes the parameterless
`tolower`, `toupper`, `ltrim`, `rtrim`, `trim`, `lineSplit`, `camelcase`, `pascalcase`, and
`snakecase`, and `quote_meta` filters plus the parameterized `startswith`, `contains`, `strstr`,
and `join` filters for `pipe<string>`. `utf8` strictly decodes a complete `pipe<byte>` stream to one string, including code points split across chunks. `sha256` hashes a complete `pipe<byte>` stream and emits 32 digest bytes; `sha1` and `md4` emit 20 and 16 digest bytes respectively for legacy interoperability only. Neither is suitable for security-sensitive use. `bin2hex` consumes a `pipe<byte>` stream and emits one
uppercase hexadecimal string; `base64encode` consumes the complete byte stream and emits one
standard Base64 string without line breaks; `base64decode` reverses the full string stream to bytes. `urlencode` and `urldecode` apply RFC 3986 UTF-8 percent encoding to URI components. `htmlspecialchars` and `htmlspecialchars_decode` use the canonical five-character text mapping; `html_entity_decode` decodes recognized named/numeric entities and preserves unknown references. `strip_tags` removes complete markup-like spans per string value; it is not an HTML sanitizer. `csvenode` encodes `pipe<string[]>` rows using quoted CSV fields and doubled embedded quotes; `csvdecode` parses each string value into a `string[]` and raises a VM exception for malformed records. `parse_str` decodes one form-style query string per input (`+` as space, strict UTF-8 `%HH`) into `map<string/string>` and keeps the last duplicate-key value. `number_format` formats `pipe<decimal>` values with a .NET decimal format string using invariant culture. Argument-free `sort` buffers a complete `pipe<int>` until closure and emits signed int32 values in ascending order, retaining duplicates and respecting bounded output; empty input produces no values. Argument-free `sort` also supports `pipe<string>` in stable ordinal UTF-16 ascending order, retaining exact contents and duplicates without culture-dependent comparison or normalization. The `sort: .Field` form stably sorts declared struct records by an exact direct int or string field, using signed-int32 or ordinal UTF-16 ascending order respectively. Records, duplicates, exact strings and input order among ties are preserved; nested paths and other key types are rejected. Argument-free `shuffle` similarly buffers `pipe<int>` or `pipe<string>` until closure, preserving exact original values without normalization or conversion, and emits a Fisher-Yates permutation with count/duplicates preserved and bounded output. It uses noncryptographic randomness, accepts unchanged order, and has no seed/replay contract. Memory grows with input count.


---


## Project Layout (Draft)


A possible repository structure (subject to change):


```text
.
├─ docs/
│  ├─ aloe-lang-spec-ja.md      # Aloe language spec (Japanese)
│  ├─ aloe-lang-spec-en.md      # Aloe language spec (English)
│  ├─ aloe-vm-spec-en.md        # AloeVM / AloeBC / GC / WASM mapping spec
│  └─ filters-standard-lib.md   # Standard filter library spec
├─ src/
│  ├─ compiler/                 # Aloe → AloeBC compiler (C#, WIP)
│  ├─ vm/                       # Native AloeVM interpreter (C#, WIP)
│  └─ aloe2wasm/                # Aloe → WASM backend (WIP)
├─ examples/
│  ├─ hello-world.aloe
│  ├─ pipes/                    # pipe + filter samples
│  └─ web/                      # Simple REST / Web-like samples
├─ tests/
│  └─ ...
├─ LICENSE
└─ README.md
```


At the moment, the specs under `docs/` are the “source of truth” for the language.


---


## Status


- ✅ **Language design**
  Drafts exist for the type system, control structures, pipes/filters, traits, exceptions, and many other details.
- ✅ **VM design**
  Drafts cover the stack model, `AloeBC` bytecode, GC strategy, WASM mapping, and the Object ownership / CallBuffer / Volatile and Committed State / Tick Quiescence / Reference Group execution model.
- ⏳ **Implementation**
  An intentionally partial compiler/VM subset is implemented and listed in the roadmap below; the broader language and WASM designs remain incomplete.
- ⏳ **Tooling**
  A subset REPL now runs in both native and browser hosts with persistent VM/heap state across successful submissions. Formatter, LSP, package manager, and broader REPL language/effect support remain future work.


Issues, PRs, and discussions from anyone interested in experimental language/runtime design are very welcome.


---


## How to Contribute


**Implementation**


- Prototypes for parts of the compiler (parser, type checker, code generation, etc.)
- A simple AloeVM interpreter
- A prototype `aloe2wasm` backend for a minimal core subset


**Spec Improvements**


- Find ambiguous parts and edge cases, and propose clarifications
- Suggest alternative syntax or semantics (ideally with concrete examples)


**Samples & Tests**


- Text tools that make good use of pipes/filters
- Simple REST / Web-style flows
- Pattern collections using traits + `with`


If you plan to propose a large design change, it helps a lot to open an Issue or Discussion
before sending a PR, so we can keep the overall language design consistent.


---


## Running the tests

The five shared libraries and the common test project target both `net8.0` and `net10.0`. Use the .NET 10 SDK and install both .NET 8 and .NET 10 runtimes to run the full suite. The default test command runs both targets; add `--framework net8.0` or `--framework net10.0` to select one. Shared-library and test outputs are separated under `bin/<TFM>/`; desktop applications, Browser REPL and MSI remain on .NET 10. This does not identify a published package or confirm integration into a consuming project.

The `Test_CompilerLib` suite includes direct WebAssembly execution tests for `aloe2wasm`. These tests require Node.js with WebAssembly support. Make `node` available on `PATH`, or set `ALOE_TEST_NODE` to the Node.js executable path, then run:

On Windows, Aloe tools use `E:\Aloe` as the default install root. Set `ALOE_INSTALL_ROOT` to an absolute path to select a separate config, data, and log root (for example, when running an isolated local build).

```text
dotnet test Test_CompilerLib/Test_CompilerLib.csproj
```

## Local package index and dependency plans

The CLI maintains an explicit local version1 JSON index. Register an existing package pair, then resolve its dependency closure:

```text
aloec package-db add --name example --version 1.0.0 --abc example.abc --meta example.meta.json --package-db package-db.json
aloec package-db list --package-db package-db.json
aloec package-db resolve --name example --version 1.0.0 --package-db package-db.json
aloec package-db remove --name example --version 1.0.0 --package-db package-db.json
```

`resolve` reads dependencies from each current metadata file. Names are case-sensitive; root versions and dependency `versionRange` values must be exact three-part numeric versions such as `1.0.0`, with no leading zeroes except `0`. Ranges, prerelease versions and wildcard selection are unsupported.

On success, stdout contains one JSON document (`formatVersion: 1`, `root`, `packages`). Packages appear once, dependencies first, with absolute `abcPath`/`metaPath`, lowercase `abcSha256`/`metaSha256`, and exact dependency identities. Ordinal dependency ordering makes unchanged inputs produce identical output. Missing packages/files, invalid metadata or identity, unsupported versions, cycles, conflicting versions of one name and I/O failures produce a diagnostic and nonzero exit status without partial plan JSON.

Resolution does not modify the index or package files. It neither downloads, installs nor executes packages. Hashes identify the bytes read; they do not establish publisher trust or guarantee that files stay unchanged after resolution. See [accepted decision0031](Documents/decisions/0031-local-exact-package-resolution-plan.md) for the precise subset and deferred registry, range, installation and source-linking policies.

## Roadmap (Rough)


### Language


Implemented compiler subset: fixed-length int[]/bool[]/byte[]/char[]/float[]/decimal[]/string[] and normal/bitfield enum or struct array literals, class/object arrays with invariant types and shared instances, Object identity equality, checked reference and primitive identity `as` casts with lazy `ifnot` fallback (no numeric conversion), primitive boxing and nonconsuming extraction in generic object/expected primitive and operator/condition contexts, named box types/constructors/ToXxx, captured named-box `with` targets and invariant named-box arrays, typed mutable Lists with Add/RemoveAt, primitive/string/enum/class-reference Sets with literals, Add/Remove/Contains/Clear, UnionWith/IntersectWith/ExceptWith, Count and foreach, primitive/enum/class-reference-keyed scalar/string/enum/class/object-reference Maps with literals, indexed reads, Set/Remove/ContainsKey/Count and insertion-order key and key/value-pair foreach, inheritable interface `void` async-operation contracts with `extends` and `implements` conformance and interface-typed async dispatch plus interface-targeted checked casts and boolean `is` tests with local parentheses/and/or/not condition narrowing with unique common-interface ancestor joins across mixed boolean proof groups and unique common-interface joins after both continuing if/else branches and false-condition exits and reachable break edges from while loops, including re-evaluation after continue and continuation facts after no-else and sole-continuation if/else exit guards with declaration-preserving write invalidation and nested conditional branch merging for if/else-if/else/while bodies, with incoming facts and write invalidation in single-pass with blocks and synchronous try/catch joins that infer a unique common nominal interface when both continuing outcomes guarantee it and while/foreach entry invalidation that distinguishes scopes and heap storage targets with per-iteration while proofs, interface elements in invariant arrays/Lists/Sets and Map keys/values, nested and mixed List/array literals/indexes/Length/foreach and shared aliases, a persistent-VM native/browser REPL subset, synchronous catch-all and ordered type-only try/catch clauses over existing exception classes with bare rethrow, local/parameter and accessible reference-field/element delete and expected-type consuming unbox with invalid checks, nested jagged arrays with shared inner references, array-expression index reads/writes, struct-element field write-back, Length and foreach; typed primitive expressions/functions and control flow; class instances with async methods and getter queries; normal/bitfield enums; value-copy structs with primitive/enum/nested-struct fields, constructors, read-only methods/getters and local `with` members. See the [language specification](Documents/en/Aloe-lang-spec.html#interface-status), [Set limits](Documents/en/Aloe-lang-spec.html#set-subset) and [Map limits](Documents/en/Aloe-lang-spec.html#map-subset). These prototypes do not establish full language conformance.

- Extend interface coverage (broader flow analysis and default members), implement trait composition, and complete structured exceptions; then verify specification coverage before freezing the 0.x syntax and type system
- Stabilize the standard filter library spec
- Flesh out the details of collections and standard types


### VM


- Finalize the instruction set (opcodes) and AloeBC binary format
- Implement the CallBuffer scheduler, VMThread pool, and Tick Quiescence
- Implement Volatile / Committed State and Tick Commit
- Implement Reference Counting / Reference Groups / Destruction Candidates
- Implement a reference C# VM including a simple GC


### WASM


The direct backend currently supports typed `int`, `float`, `bool`, `byte`, and UTF-16 code-unit `char` scalar values with structured control flow. Type-preserving identity casts for these scalar types are no-ops; `ifnot` cast fallbacks remain unsupported. `float` maps to IEEE 754 binary32 and supports arithmetic (`+`, `-`, `*`, `/`) and comparisons with `float`/`int`/`byte` operands; mixed arithmetic follows VM binary32 promotion, while mixed comparisons preserve int32 ordering. Non-finite values, division by zero, and multiplication/division underflow trap. Float modulo follows VM binary32 semantics, including signed zero and an infinite divisor returning the finite dividend; NaN operands and zero divisors trap. Decimal operations and numeric conversions remain unsupported. The backend also supports the implemented int32 arithmetic/bitwise subset; `byte` arithmetic promotes to checked int32, mixed `byte`/`int` comparisons use int semantics, and exported int parameters are checked against signed-int32 bounds and bool parameters against 0/1 and byte parameters against 0..255, while exported char parameters must be UTF-16 code units in 0..65535 (including surrogates); out-of-range i32 values trap. The no-argument `tick()`, `GC.require()`, and `GC.finish()` controls lower as no-ops while this subset has no heap objects or async call buffers. `GC.debug` bool state uses a private per-instance global initialized to false and persists across calls and writes completed before a trap; instances remain independent. Scalar `print` uses explicit synchronous `aloe.print_int(i64)`, `print_float(f32)`, `print_bool(i32)`, `print_byte(i32)` and `print_char(i32)` callbacks supplied by the host (decision 0029). Only used kinds are imported; each callback represents one output line, with formatting and destination controlled by the host. No-print modules remain import-free. GC phase/timing log output, host debug settings, other host runtime imports, arrays, and object values remain unsupported.

- Broaden direct WASM execution without going through an interpreter


### Tooling


- CLI tools (e.g. `aloec`, `aloevm`)
- Formatter / syntax highlighting
- A simple package management mechanism (can be very minimal at first)


---


## License


Aloe is planned to be released under the **MIT License**.
See the `LICENSE` file in the repository for details.
