# 0056 Switch pipeline stage `|: switch (key) : value => filter, ..., default => filter`

Status: Accepted
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user responses「switch (value) : value_A => filter_A, value_b => filter_B, value_C => filter_C, default => default_filter ではどうか」「defaultは_（アンダーバー）でも代用できるといいね」
Implementation status: Not implemented (after 0055). Product acceptance requires `.local/switch-stage-<date>/acceptance.json`.
Rubric: F17 (Switch filter contract and execution)

## 決定

標準フィルター仕様4.2の未定義の `filter switch` を、0055と同じパイプライン段の構文として定義します。

```aloe
items
    |: switch (.Kind) : Kind.Note => trim, Kind.Raw or Kind.Bin => drop, default => tolower
    | out;

words |: switch (.) : "a" => toupper, _ => drop | out;   // _ は default と同じ
```

1. 構文: `|: switch (キー) : 値 => 動作 { , 値 => 動作 } [ , default => 動作 ]`。段は次の `|` または `|:` までで、途中の改行は自由です。
2. キー: 要素自身 `.` または構造体フィールド `.Field`。
3. 値: キーと同じ型のリテラル（int、decimal、float、char、string、bool）またはenumメンバー。1分岐に複数の値を `or` で並べられます（`A or B => drop`）。同じ値の重複はコンパイルエラーです。
4. 判定: キーと等しい値を持つ最初の分岐の動作だけを適用します。等しさは決定0054の型ごとの比較規則に従います。
5. `default => 動作`: どの値にも一致しない要素に適用します。末尾に1つだけ置けます。`_ => 動作` は `default => 動作` と同じ意味の短縮形で、両方を同時に書くことや、末尾以外に置くことはコンパイルエラーです。省略時は加工せずにそのまま流します（0055と同じ）。
6. 動作: 0055と同じく `drop` と1件ずつ処理する文字列の標準フィルターに限り、入力順を保ちます。
7. 用語: if系の段（0055）は `else`、switch段は `default`（または `_`）を使います。
8. 実行: コンパイラーが等値比較の分岐へ変換し、0055と同じVM組み込み番号-44で評価します。新しいVM命令やバイトコード形式の変更はありません。

## 受入テスト（実装時）

一致・不一致・default・`_`・省略時の通過、`or` による複数値、要素自身とフィールドのキー、全リテラル型とenum、入力順、空入力、bounded pipe、AloeBC round-trip。拒否境界として、値の型不一致、重複値、defaultと`_`の併用、default以外の後続分岐、未対応の動作。

## 後続

分岐ごとの出力先への振り分け、範囲やパターンによる一致。
