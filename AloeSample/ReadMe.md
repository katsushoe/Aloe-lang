# AloeSample - Source-to-VM Demo


This sample is the current minimal end-to-end Aloe execution path.


```text
Aloe source
  -> AloeLexer
  -> AloeCompiler (parser + direct code generation)
  -> Module / Instruction
  -> AloeVm
  -> process exit code
```


## Supported source subset


- `function main(args: string[]): int`
- user-defined `function` declarations (`int` / `string` / `bool` parameters; `int` / `string` / `bool` / `void` returns)
- function calls, forward references, and recursion
- `var name = expr;`
- `let name: int|string|bool = expr;`
- local reads and assignment
- `if / else if / else`
- `while`
- `break;` / `continue;`
- `print(expr);`
- `return expr;` / `return;`
- early `return` from nested `if` / `while` blocks
- int / string / bool
- `+ - * / %`
- `== != < <= > >=`
- `not`, `and`, `or` (`and / or` short-circuit)
- lexical block scope


A non-`void` function must return a value on every reachable path. If every branch of an `if / else` returns, no trailing `return` is required. A `void` function gets an implicit return at a reachable end. The current definite-return analysis is conservative and does not treat a `while` loop itself as guaranteed to return.


## Samples


```text
dotnet run --project AloeSample -- AloeSample/hello.aloe
dotnet run --project AloeSample -- AloeSample/sum.aloe
dotnet run --project AloeSample -- AloeSample/fizzbuzz.aloe
dotnet run --project AloeSample -- AloeSample/control-flow.aloe
dotnet run --project AloeSample -- AloeSample/functions.aloe
```


`control-flow.aloe` exercises `and / or / not` and `break / continue`. `functions.aloe` exercises parameters, return values, `void` functions, forward references, recursion, and early returns end to end.


## Current limitations


- `for / do / switch`
- discarding a non-void function result as an expression statement
- function overloading
- classes / structs / properties
- Object / CallBuffer / Tick
- pipes / filters
- AloeBC binary serialization