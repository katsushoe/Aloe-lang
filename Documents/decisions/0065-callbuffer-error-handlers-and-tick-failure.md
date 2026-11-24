# 0065 Single CallBuffer, CallBuffer.onError and Tick failure

Status: Accepted (revised 2026-11-23)
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user responses「CallBufferに例外ハンドラを定義できるようにして、その中でtickが失敗するか、処理するかをプログラマが定義できるようにしたい。デフォルト…tick全体が失敗する」「CallBuffer.onError = handler にしたい」「インスタンスCallBufferは複雑さを増すだけで運用上は必要ないと考えます」
Implementation status: Not implemented. Product acceptance requires `.local/callbuffer-errors-<date>/acceptance.json`.
Rubric: L32 (Async exception propagation and agreed failure/rollback contract). Related: 0051 (Task), 0059 (exception values), H11, H12.
Revision note: the first version (class-level `on error` only, with Instance/Static CallBuffers) was replaced by the single-CallBuffer model; class-level `on error` was then reinstated as the first handler tier before `CallBuffer.onError` (user response「クラス内の「on error」も可能にして…」).

## 問題

言語仕様第16章とVM仕様は、asyncメソッドの未捕捉例外、Tick失敗時の状態、未処理の呼び出し、ホストの副作用を未解決としていました。試作VMは、失敗時に残りの呼び出しを捨てて最初の例外を `tick()` から投げ、以後のTickをすべて拒否する（VMが使用不能になる）動作でした。

仕様19.3はObjectごとのInstance CallBufferと型ごとのStatic CallBufferを定義していましたが、意味上必要なのは「同じ対象への呼び出しの順序と排他」だけで、試作VMもInstance側を1本の待ち行列で実装しています。

## 決定

### 1. CallBufferを1つに統一する

- VM全体でCallBufferは1つです。Instance CallBuffer / Static CallBufferという区別は仕様から廃止します。
- 保証は次のとおりです（意味は従来と同じ）。
  - 同じObjectへのpublic asyncメソッド呼び出しは、積まれた順に1つずつ実行されます。
  - 同じ型へのpublic static asyncメソッド呼び出しは、積まれた順に1つずつ実行されます。
  - 異なるObject・型の呼び出しは並列に実行され得て、その間の順序は決まりません。
  - Tickは、処理中に積まれた呼び出しを含め、CallBufferが空になるまで続きます（Tick Quiescence）。
- Taskの通知（決定0051）は、Tick内の処理順の規則を保ったまま、例外については本決定のハンドラーで扱います。
- VMの実装と既存プログラムの動作は変わりません。仕様19.3・VM仕様・READMEの説明と用語を書き換えます。

### 2. CallBuffer.onError

```aloe
exception OutOfStock { field itemId: int; }

function onAsyncError(e: Exception, call: CallInfo): void {
    if (e is OutOfStock) {
        print("skip: " + call.TypeName + "." + call.MethodName);   // 正常終了 → 処理済み、Tick続行
    } else {
        throw;                                                     // 投げ直し → Tick全体が失敗
    }
}

function main(args: string[]): int {
    CallBuffer.onError = onAsyncError;
    var shop = new Shop();
    shop.buy(1);
    try { tick(); } catch (e: Exception) { print("Tick failed: " + e.Message); }
    CallBuffer.onError = null;     // 既定（Tick全体の失敗）に戻す
    tick();
    return 0;
}
```

- `CallBuffer` はVMに1つの組み込みの存在で、`onError` プロパティだけを持ちます。
- 代入できるのは、シグネチャが `(e: Exception, call: CallInfo): void` の自由関数の名前、または `null` です。一般的な関数値・クロージャー・Objectのメソッド参照は導入しません。
- 設定できるのはObjectの外のコード（mainと自由関数）だけです。classのメソッド・コンストラクター・Taskの中での代入はコンパイルエラーです。Tickの処理中（CallBufferの実行中）に代入した場合はVMエラーです。
- `CallInfo` は組み込みの構造体で、`Kind: string`（"instance"、"static"、"task"）、`TypeName: string`、`MethodName: string` を持ちます。引数の値は含めません。
- ハンドラーは呼び出し元の例外の値を受け取ります。例外の型による分岐のため、0059の例外の値に対して `is` / `as` を使えるよう拡張します。

### 3. クラス内の on error（第1段のハンドラー）

```aloe
class Shop {
    field missCount: int;
    public async method buy(itemId: int): void { ... }

    on error (e: OutOfStock, call: CallInfo) {      // 一致 → 処理済み、Tick続行
        this.missCount = this.missCount + 1;
    }
    on error (e: Exception) {                       // 投げ直し → CallBuffer.onError へ
        throw;
    }
    static on error (e: Exception) { throw; }       // static async メソッド用
}
```

- `on error (e: 例外型 [, call: CallInfo]) { ... }` をclassに宣言できます。そのクラスのインスタンスへのpublic asyncメソッド呼び出しで発生した未捕捉の例外に適用されます。`static on error (...)` はそのクラスのpublic static asyncメソッド、Task宣言内の `on error (...)` はそのTaskの通知処理に適用されます。
- 複数書いた場合は、0059のcatchと同じくソース順に型を照合し、最初に一致した1つだけを実行します。
- 派生クラスでは、派生クラス自身のハンドラーをソース順に照合し、一致しなければ基底クラスのハンドラーを照合します。
- クラス内のハンドラーは、そのObject（staticの場合は型）の文脈で、そのObjectの呼び出しと同じ直列の順序で実行され、そのObjectの状態を変更できます。

### 4. 処理の順序（エスカレーション）

1. 呼び出し先のクラス（Task・staticを含む）に一致する `on error` があれば、それを実行します。正常に終了すれば処理済みとし、Tickを続けます。
2. 1が未定義（型が一致しない場合を含む）、または1の中で `throw;`・新しい例外のthrow・未捕捉の例外が起きた場合は、`CallBuffer.onError` を呼びます。このとき渡す例外は、1で新しい例外が起きた場合はその例外、それ以外は元の例外です。正常に終了すれば処理済みとし、Tickを続けます。
3. 2が未設定（`null`）、または2の中で `throw;`・新しい例外のthrow・未捕捉の例外が起きた場合は、Tick全体の失敗とし、その例外をmainへ伝えます。

共通の規則:

- 処理済みの場合、失敗した呼び出しが途中まで行った変更は、catchと同じく残ります。
- `CallBuffer.onError` はObjectの状態を直接変更できません。必要ならpublic asyncメソッドを呼んで依頼でき、その呼び出しは同じTick内で処理されます。
- 並列実行中に複数の失敗が起きた場合も、`CallBuffer.onError` の呼び出しはVM内で1つずつ直列に行います。

### 5. Tick全体の失敗

- 全Object・Taskの確定前の状態（Volatile State）を捨て、直前の確定状態（Committed State）に戻します。
- そのTickで未処理の呼び出しと、そのTick中に積まれた呼び出し・通知を捨てます。
- 例外を `tick()` から呼び出し元（main）へ投げます。mainのtry/catchで捕捉できます。
- VMは失敗状態を解除し、次の `tick()` から通常どおり使えます。
- 複数の失敗が起きた場合は、最初に記録した1件を投げ、メッセージに失敗件数を含めます。
- ホストの副作用は巻き戻しません（実行の時点はH12で決定）。

### 6. その他

- asyncメソッドの本体内でもtry/catch/finallyを使えるようにします（本体内で処理された例外はハンドラーに届きません）。
- 実装: `onError` の設定・解除と例外値の `is` / `as` のためのsyscallを末尾に追加し、確定前の状態の破棄と失敗状態の解除をVMに追加します。既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません。

## 受入テスト（実装時）

クラス内on errorの型照合・派生クラスの照合順・static/Task用、クラス内→CallBuffer.onError→Tick失敗の3段の伝搬（未定義・投げ直し・新しい例外の各経路）、CallBuffer.onErrorによる処理済み・投げ直し・ハンドラー内例外、`null` による既定の失敗、CallInfoの各値（instance・static・task）、例外値の `is` / `as`、ハンドラーからのasync呼び出し、全Objectの状態の巻き戻し、残りの呼び出しの破棄、mainでの捕捉と次Tickの継続、async本体内のtry/catch、並列時の複数失敗とハンドラーの直列実行、Object内・Tick中の代入の拒否、シグネチャ不一致の拒否、同じ対象への呼び出しの順序と排他（既存動作の維持）、AloeBC round-trip。両TFM全テスト、Release、英日の言語仕様第16章・第19章、VM仕様、READMEの更新。
