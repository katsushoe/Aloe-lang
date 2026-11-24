# 0064 Async methods return no values

Status: Accepted (option B)
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user response「B」 (after「asyncの戻り値は導入しない で終わりなのでは？」)
Implementation status: Existing compiler behavior already rejects value-returning async declarations; acceptance requires a consolidated regression test set and `.local/async-no-values-<date>/acceptance.json`.
Rubric: L40 (Value-returning async semantics beyond current void subset), closed by confirming the existing principle.

## 経緯

評価台帳のL40は「void以外の値を返すasyncの取り決め」として作られましたが、言語仕様には既に「AloeのasyncはC#のasync/awaitではなく、対象ObjectのCallBufferへ呼び出しを積むことを意味する」「AloeはFuture、Promise、Task<T>、await、asyncの戻り値を導入しない」と明記されています。L40は新機能の設計ではなく、この原則を取り決めとして確定し、検証する項目です。

## 決定

1. async（instance public async、public static async、interfaceのasync取り決め、Taskのメソッド）は値を返しません。宣言できる戻り値の型は `void` だけです。
2. Future、Promise、`Task<T>`、await、返信先指定などの「asyncの結果を呼び出し元へ返す仕組み」は導入しません。
3. 結果の伝え方は既存の2通りです。
   - 呼び出し元が、後のTickで相手のpublic property（Committed Stateの問い合わせ）を読む。
   - 相手が、明示的に受け取った参照の相手（呼び出し元など）のasyncメソッドを呼ぶ。
4. 値を返すasyncの宣言、async呼び出しを値として使う式（代入・引数・戻り値・演算への使用）はコンパイルエラーです（現行の動作を維持）。

## 受入（追加するもの）

instance public async、public static async、interfaceのasync取り決め、Taskのメソッドについて、値を返す宣言がコンパイルエラーになること、async呼び出しを値として使えないこと、propertyとasyncの呼び返しで結果を伝える例が動くことを、まとめた回帰テストで確認します。英日の言語仕様に、本決定への参照を追記します。新しいVM機能・syscall・AloeBC変更はありません。
