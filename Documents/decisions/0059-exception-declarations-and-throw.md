# 0059 Exception declarations, exception values and throw expressions

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「A」
Implementation status: Implemented (2026-11-25). Syscalls 65 `ExceptionThrow` and 66 `CaughtExceptionValue`, `exception:Name` catch descriptors, `Test_CompilerLib/ExceptionValueTests.cs`, and the English/Japanese language specification chapter 16. The example below ignores the int result of `find("a")`, which the language still rejects; the tests print it instead. Product acceptance: `.local/exception-values-20261125/acceptance.json`.
Rubric: L30 (Exception values, catch-variable bindings and throw expressions); extends accepted decisions 0044 and 0049.

## 問題

同期のtry/catch/finally、型指定catch、決定0049の `catch (e: Exception)` による `ExceptionInfo`（Message、TypeName）のコピー、`throw;` は実装済みです。しかし `throw 式;`、利用者定義の例外型、例外への独自情報の付加は未定義・未実装でした。

## 決定

1. 例外宣言を追加します。

   ```aloe
   exception AppError { field code: int; }
   exception NotFound : AppError { field key: string; }

   function find(key: string): int {
       throw NotFound { Message: "not found", code: 404, key: key };
   }

   function main(args: string[]): int {
       try { find("a"); }
       catch (e: NotFound) { print(e.key); print(e.code); print(e.Message); }
       catch (e: Exception) { print(e.TypeName); }
       return 0;
   }
   ```

2. 例外は構造体と同じ値型です。すべての例外型は `Message: string` と `TypeName: string` を自動で持ちます（宣言順で先頭、`TypeName` は型名で自動設定され、リテラルでは指定できません）。独自フィールドは構造体の規則に従い、参照型フィールドや再帰的な配置は構造体と同様に拒否します。初期範囲ではメソッドを持ちません。
3. 継承: `exception B : A` で例外型どうしの単一継承ができます。継承元を省略した場合は組み込みの `Exception` を継承したものとします。派生型は基底型のフィールドを先頭から受け継ぎます。組み込みの9種（Exception、SystemException、ZeroDivisionException、InvalidReferenceException、InvalidCastException、OverflowException、ArgumentException、IndexOutOfRangeException、VmException）を継承元にできます。
4. throw: `throw 例外値;` は例外型の値（リテラル、変数、引数、関数の戻り値）を投げます。例外型以外の値はコンパイルエラーです。組み込みの9種も `throw ArgumentException { Message: "..." };` で投げられます。既存の `throw;`（再送出）と `as T ifnot throw` は変更しません。
5. catch: `catch (e: T)` は、投げられた値の型がTまたはTの派生型なら一致し、`e` は型Tとして独自フィールドを含む値のコピーを受け取ります（派生型を基底型で受けた場合は基底型のフィールドだけが見えます）。`catch (e: Exception)` と組み込み型によるcatchは、利用者定義の例外でも従来どおり `ExceptionInfo` として見えます。ホスト・VMが発生させる例外（0除算など）の扱いは変更しません。照合順序は既存どおりソース順です。
6. finallyと例外の置き換え、未捕捉時の扱いは決定0044と既存の規則に従います。未捕捉の利用者定義例外は、型名とメッセージを含むVM例外としてホストへ報告します。
7. 実装: 例外値は構造体値として扱い、投げる処理と型照合のための組み込みsyscallを末尾に追加します。既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません（新機能を使うプログラムには更新後のVMが必要です）。

## 対象外（後続）

原因の連結（inner exception）、スタックトレース、例外型のメソッド、非同期処理（CallBuffer・Tick）への例外伝播とロールバック（L32）、WASM直接出力（W15）、クラスのオブジェクトを例外として投げること。

## 受入テスト（実装時）

宣言・継承・継承元省略、独自フィールドの読み書きとコピー、throwの各式、派生型の基底型catch、`catch (e: Exception)` での `ExceptionInfo` 表示、組み込み型のthrow、ソース順照合、ネスト・再送出・finallyとの組み合わせ、未捕捉時の報告、AloeBC round-trip。拒否境界として、例外型以外のthrow、`TypeName` の指定、参照型フィールド、未定義の継承元、循環継承、例外型の通常構造体としての誤用。両TFM全テスト、Release、英日の言語仕様第16章の更新。
