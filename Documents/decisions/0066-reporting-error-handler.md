# 0066 set_error_handler as a reporting-only final handler

Status: Accepted (option A)
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user response「A」
Implementation status: Not implemented (after 0058 imports and 0065). Product acceptance requires `.local/error-handler-<date>/acceptance.json`.
Rubric: H11 (Source error-handler registration and dispatch). Related: 0058 (imports), 0059 (exceptions), 0065 (CallBuffer.onError), H10 (aloe.log). Amended by 0067 (host API errors are ordinary exceptions).

## 問題

標準ライブラリ仕様第6章は `set_error_handler(handler: function(LogLevel, string, Exception?): void, levels)` を定義していますが、言語には関数型の引数とnull許容型がありません。呼ばれる場面とログレベルの対応も「VM仕様で定める」とされ、決定0065の `CallBuffer.onError` との役割分担も未定義でした。

## 決定

1. 役割の分担
   - その場で処理する: try/catch、クラス内の `on error`（0065）。
   - Tickを続けるか決める: `CallBuffer.onError`（0065）。
   - 最後に報告を受ける: `set_error_handler`（本決定）。終了を止めたり処理を続行させたりはできません。
2. API（名前空間 `aloe.system`、importなしで使用可能）

   ```aloe
   function onFatal(level: LogLevel, message: string, ex: ExceptionInfo): void {
       print("[" + level + "] " + message + " " + ex.TypeName);
   }

   function main(args: string[]): int {
       set_error_handler(onFatal, LogLevel.Error | LogLevel.Fatal);
       ...
       clear_error_handler();
       return 0;
   }
   ```

   - `set_error_handler(handler, levels)`: `handler` はシグネチャが `(level: LogLevel, message: string, ex: ExceptionInfo): void` の自由関数の名前です（0065と同じく、一般的な関数値は導入しません）。`levels` は省略時 `LogLevel.All` です。最後に登録したものが有効です。
   - `clear_error_handler()`: 登録を解除し、VMの既定の動作（標準エラーへの出力と終了）に戻します。
   - 設定・解除できるのはObjectの外のコード（mainと自由関数）だけです。classのメソッド・コンストラクター・Taskの中ではコンパイルエラー、Tickの処理中ではVMエラーです。
   - 例外を伴わない通知では、`ex` は `Message` が空文字列、`TypeName` が空文字列の `ExceptionInfo` です（null許容型は導入しません）。
3. 呼ばれる場面とレベル
   - mainから例外が抜けたとき（Tick失敗を含む）: `Fatal`。ハンドラーの実行後、VMは終了コード1で終了します。
   - VMの致命的エラー（バイトコード検証エラー、内部エラー等）: `Fatal`。ハンドラーの実行後に終了します。
   - （削除: 決定0067により、ホストAPIのエラーは通常の同期の例外として扱い、ここでは扱いません。）
   - 呼ばれない場面: `log()` など aloe.log の呼び出し、mainのtry/catchで捕捉されたTick失敗や例外、`on error` / `CallBuffer.onError` で処理済みになった例外、`exit()` による通常の終了。
   - `levels` に含まれないレベルの場合は呼ばれず、既定の動作だけが行われます。
4. ハンドラー内のエラー: ハンドラーの中で例外が起きた場合は無視し、ハンドラーを再度呼ばずにそのまま終了処理を続けます。
5. `LogLevel`（aloe.log、H10で詳細を定義）: ビット単位のenumとして `Trace`、`Debug`、`Info`、`Warn`、`Error`、`Fatal` と、全ビットの `All` を持ちます。`set_error_handler` の引数として使う場合も、0058の規則でimportするか完全修飾名 `aloe.log.LogLevel` を使います。
6. 実装: ハンドラーの登録・解除と終了時の呼び出しのためのsyscallを末尾に追加します。既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません。REPLでは、入力ごとのエラーを従来どおり表示し、REPL自体は終了しません（ハンドラーは呼びますが終了はしません）。

## 受入テスト（実装時）

mainから抜けた例外・Tick失敗でのFatal呼び出しと終了コード1、VMの致命的エラー、levelsによる選別、clearによる既定動作、捕捉・処理済みの例外や log() で呼ばれないこと、ハンドラー内の例外で再帰しないこと、シグネチャ不一致・Object内・Tick中の設定の拒否、例外なしの通知での空のExceptionInfo、REPLでの動作、AloeBC round-trip。両TFM全テスト、Release、英日の標準ライブラリ仕様第6章の更新。
