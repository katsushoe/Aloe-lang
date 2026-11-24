# 0054 Standard filter sort: remaining scalar kinds and ordering contract

Status: Accepted
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, under the user's standing approval「推奨案は自動採用で進める (Recommended)」 for small standard-filter/library contracts.
Implementation status: Implemented; product acceptance in `.local/sort-kinds-20261122/acceptance.json`.
Rubric: F22 (Remaining ordering element/key kinds and comparator contract)

## 問題と現在の仕様

採用済みのsortは、引数なしのint・string・byte・bool要素と、構造体のint・byte・bool・stringフィールドをキーにした昇順だけでした（0025、0026、0030、0033、0035、0036、0039）。decimal・char・floatの要素とキー、降順、比較規則全体の契約は未定義でした。また `pipe<char>` と `pipe<float>` は宣言できませんでした。

## 決定

1. `pipe<char>` と `pipe<float>` を要素型として追加します。floatは有限値だけを受け付けます（既存のfloat領域と同じ）。
2. 引数なしsortを `pipe<decimal>`・`pipe<char>`・`pipe<float>` に広げます。
3. 構造体キーsortのキー型に char・float・decimal を追加します。
4. 降順を `source |: sort: desc | result;` と `source |: sort: .Field desc | result;` で指定できます。既存の全スカラー型とキー型に適用します。
5. 比較規則（comparator契約）:
   - int: 符号付きint32、byte: 0〜255、bool: false < true、string: UTF-16の序数比較、char: UTF-16コード単位、float: 数値順（-0.0と0.0は等しい）、decimal: 数値順（1.0と1.00は等しい）。
   - 等しい要素は入力順を保ちます（昇順・降順とも安定）。値の表現（decimalの桁、floatの符号付きゼロ、文字列の正規化形）は変更しません。
   - 大文字小文字の無視、カルチャー依存比較、ユーザー定義の比較関数、複数キー、入れ子のフィールドパスは標準sortの契約に含めません。必要な場合は別のフィルター契約とします。
6. 互換性: 既存の昇順int・string・byte・boolは従来の組み込み番号（-32、-36、-38、-40）とバイトコードをそのまま使います。新しい要素型と降順は、要素型と順序を文字列引数（JSON `{"element":..., "descending":...}`）で渡す組み込み番号 -43 を使います。キーsort（-35）のスキーマは省略可能な `fieldType` 値 char/float/decimal と `descending` を受け付け、旧スキーマは昇順として解釈します。

## 受入テスト

`Test_CompilerLib/SortKindsTests.cs`：decimal・char・floatの昇順と降順、安定性と表現の保持、空入力、既存4型の降順、char・float・decimalキーの昇順と降順、型不一致・不明な引数・未対応キーの拒否、`pipe<char>`・`pipe<float>` への誤った型の書き込み拒否、既存の昇順sortが従来の番号を保つこと。いずれもAloeBC round-trip後に実行します。

## 対象外

shuffleの新しい要素型、WASM直接出力でのpipe/filter（W16）、ユーザー定義の比較関数。
