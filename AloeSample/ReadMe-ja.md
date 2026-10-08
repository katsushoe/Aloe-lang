# AloeSample - Source-to-VM 実行サンプル


このサンプルは、現在実装されている Aloe の最小エンドツーエンド実行経路です。


```text
Aloe source
  -> AloeLexer
  -> AloeCompiler（Parser + 直接 CodeGen）
  -> Module / Instruction
  -> AloeVm
  -> process exit code
```


## 現在対応している構文


- `function main(args: string[]): int`
- ユーザー定義 `function`（`int` / `string` / `bool` 引数、`int` / `string` / `bool` / `void` 戻り値）
- 関数呼び出し、前方参照、再帰呼び出し
- `var name = expr;`
- `let name: int|string|bool = expr;`
- ローカル変数参照と代入
- `if / else if / else`
- `while`
- `break;` / `continue;`
- `print(expr);`
- `return expr;` / `return;`
- `if` / `while` の内側からの早期 `return`
- int / string / bool
- `+ - * / %`
- `== != < <= > >=`
- `not`, `and`, `or`（`and / or` は短絡評価）
- ブロックのレキシカルスコープ


非 `void` 関数は、到達可能なすべての経路で値を返す必要があります。`if / else` の全分岐が `return` する場合は末尾の `return` を省略できます。`void` 関数は到達可能な末尾に暗黙の `return` が入ります。現段階の definite-return 判定は保守的で、`while` 自体を「必ず return する」とはみなしません。


## サンプル


```text
dotnet run --project AloeSample -- AloeSample/hello.aloe
dotnet run --project AloeSample -- AloeSample/sum.aloe
dotnet run --project AloeSample -- AloeSample/fizzbuzz.aloe
dotnet run --project AloeSample -- AloeSample/control-flow.aloe
dotnet run --project AloeSample -- AloeSample/functions.aloe
```


`control-flow.aloe` は `and / or / not` と `break / continue` を、`functions.aloe` は引数・戻り値・`void` 関数・前方参照・再帰呼び出し・早期 `return` を Source-to-VM で確認する E2E サンプルです。


## 現在の未対応範囲


- `for / do / switch`
- 値を返す関数呼び出しを式文として捨てる構文
- 関数オーバーロード
- class / struct / property
- Object / CallBuffer / Tick
- pipe / filter
- AloeBC バイナリ直列化