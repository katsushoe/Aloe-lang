# 0068 Result<T>, filter outputs and ROP at boundaries

Status: Accepted
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user responses「boundが例外を投げた際は自動でResultが失敗値を格納するようにできない？（filterのon errorを定義して、そこで変換する。未定義なら自動変換）前提としてはfilterはResult型を返す」「boundはbound(in, out)なのでは」「処理があっちこっち飛んでプログラマが混乱しない？」「それでいい」「失敗した要素はinに入ってこない仕様にしたら？」「Resultには失敗した際どのfilterで失敗したか分かる情報を入れておく必要があるね」
Implementation status: Not implemented (after 0055 and 0059). Product acceptance requires `.local/result-rop-<date>/acceptance.json`.
Rubric: L36 (Result/ROP source semantics and typed pipeline propagation).

## 問題

言語仕様18.2は「filter / producer / consumer のboundはResultを返すのが必須」と定めていましたが、実装済みのfilterは `bound(input, output)` で出力pipeへ書き込む形で戻り値がなく、矛盾していました。`Result<T>` と `Error` の定義は日本語版（18.2.1）にだけあり、英語版にはありませんでした。

## 決定

1. 型
   - `Result<T>` は組み込みの値型で、`Ok(value: T)` か `Err(error: Error)` のどちらか一方を持ちます。`IsOk: bool`、`Value: T`、`Error: Error` で判定・取り出しをします。誤った状態での取り出しは `InvalidOperationException` です。作り方は `Result.Ok(値)`、`Result.Err(error)` です。
   - `Error` は組み込みの値型（構造体）で、`code: string`、`message: string`、`cause: ExceptionInfo`、`filter: string`、`stage: int` を持ちます。`filter` と `stage` は失敗した場所を表し、プログラマは設定できず、失敗値がfilterの出力に最初に書かれた時点でVMが設定します。`filter` はそのfilterの完全修飾名（決定0058）、`stage` はパイプライン文の中でのそのfilterの位置（先頭の段が1）です。filterの外（mainなど）で作って書いた失敗値は `filter` が空文字列、`stage` が0です。素通しされる失敗値の `filter` / `stage` は変更しません。例外を伴わない場合の `cause` は各項目が空文字列です。例外をErrに変換する場合は `cause` を設定します（仕様18.2.1.3の「cause必須規則」を維持）。エラーコードは文字列で、ドメインごとに定義します（18.2.1.4を維持）。
2. filterの出力
   - filterの出力は `pipe<Result<T>>` です。boundの形は従来どおり `bound(input, output)` です。
   - `output.write(値)` で `T` の値を書くと `Ok(値)` として書かれます。`output.write(Result.Err(error))` で失敗値を書けます。
   - 「boundはResultを返す」は「filterの出力はResultのpipe」と読み替え、仕様18.2を修正します。producer / consumer も同じ規則です。
3. boundの中には暗黙の動作を入れません。例外は通常の言語の規則どおりに伝わり、自動の捕捉・継続はしません。要素ごとに失敗値にして処理を続けたい場合は、boundの中で明示的にtry/catchを書きます。

   ```aloe
   filter ParseAge {
       in:  pipe<string>;
       out: pipe<Result<int>>;
       bound(input, output) {
           foreach (text in input) {
               try { output.write(int.parse(text)); }
               catch (e: FormatException) {
                   output.write(Result.Err(Error { code: "age.format", message: text, cause: e }));
               }
           }
           output.close();
       }
       on error (e: Exception): Error {
           return Error { code: "age.unexpected", message: e.Message, cause: e };
       }
   }
   ```

4. boundの外へ例外が抜けた場合（境界での変換）
   - filterの `on error (e: 例外型): Error` を、0059のcatchと同じくソース順に照合し、一致したものが返す `Error` を使います。
   - 一致しない、または未定義の場合は自動で `Error { code: "aloe.filter.exception", message: e.Message, cause: e }` に変換します。`on error` の中で例外が起きた場合も、その例外を自動変換します。
   - その `Err` を1件 `output` に書き、出力を閉じてfilterを終了します（その後の入力は処理しません）。
5. 失敗値の素通し（ROPのレール）はfilterの入口で行います。
   - 失敗した要素（`Err`）は、次のfilterの `in` に入りません。filterの入口でVMが受け取り、bound を通さずにそのfilterの出力へそのまま流します。boundには成功した値（`Ok` の中身 `T`）だけが届きます。

     ```aloe
     // ParseAge: in pipe<string>, out pipe<Result<int>>
     // CheckAdult: in pipe<int>, out pipe<Result<int>>
     lines | filter(ParseAge) | filter(CheckAdult) | results;   // results: pipe<Result<int>>
     ```

   - 失敗値も受け取りたいfilter（失敗の集計・記録など）は、自身の入力を `in: pipe<Result<T>>` と宣言します。この場合だけ、`Ok` と `Err` の両方が `in` に入ります。受け取るかどうかはfilterの宣言に書かれます。
   - 素通しする失敗値は、入口に届いた時点で出力へ流します。1件ずつ処理するfilterでは入力の順序が保たれます。入力を溜めてから出すfilter（sort等）では、失敗値が先に出力されることがあります。
   - 失敗値が流れ得るパイプラインの最終の出力先は `pipe<Result<T>>` でなければならず、`pipe<T>` へつなぐとコンパイルエラーです（失敗値が黙って消えることを防ぎます）。
   - boundの中では、foreachなどが型によって `Err` を暗黙に読み飛ばす動作は設けません（`in` の型どおりの値だけが届きます）。
6. 標準フィルターと既存のfilter: 既存の標準フィルターと、`pipe<T>`（Resultでない）を出力するユーザー定義filterは、移行のため当面そのまま使えます。Result化の対象範囲と移行の手順は実装時に定め、仕様に記載します。
7. 仕様の同期: 英語版の言語仕様に18.2.1（Result / Error）を追加し、日英を一致させます。

## 受入テスト（実装時）

Ok / Errの作成と判定・取り出し、誤った取り出しの例外、`output.write` の自動Ok化と明示Err、bound内の明示try/catchによる要素ごとの継続、boundから抜けた例外の `on error` 照合・自動変換・on error内の例外、Err書き込み後の出力のclose、入口での失敗値の素通しと順序、`in: pipe<Result<T>>` 宣言による失敗値の受け取り、最終出力先が `pipe<T>` の場合のコンパイルエラー、`Error.filter` / `stage` の自動設定と素通し時の保持（同じfilterを2段で使う場合を含む）、producer / consumer、AloeBC round-trip。両TFM全テスト、Release、英日の言語仕様第18章の更新と同期。
