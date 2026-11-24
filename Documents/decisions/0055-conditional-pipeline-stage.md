# 0055 Conditional pipeline stage `|: (condition) => filter`

Status: Accepted
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user responses「if / else / elseifパイプを全面的に刷新」「|: (if-condition) => (applied filter) という文法はどうか」「推奨案ですすめて」
Implementation status: Not implemented. Product acceptance requires `.local/conditional-stage-<date>/acceptance.json`.
Rubric: F16 (Conditional if/elseif/else filter contract and execution)

## 問題

旧仕様（標準フィルター4.1）は `|: if:` / `|: elseif:` / `|: else` を1つの複合フィルターとして並べるだけで、出力先が1つのため、`else` があると全要素が通過し、ない場合も条件のORと同じになり分岐の意味がありませんでした。実装はありません。

## 決定

標準フィルター名 `if` / `elseif` / `else` を廃止し、パイプライン段の構文として置き換えます。

```aloe
lines
    |: (. != "") => toupper
    |: (.Kind == Kind.Note) => trim elseif (.Kind == Kind.Raw) => drop else => tolower
    | out;

users |: (.Age >= 20) | adults;   // => を省略すると条件に合う要素だけを残す
```

1. 構文: `|: (条件) => 動作 { elseif (条件) => 動作 } [ else => 動作 ]`、または `|: (条件)`。
2. 判定: 各要素について上から順に条件を評価し、最初に真になった1つの動作だけを適用します（排他）。
3. 条件に合わない要素: `else` がなければ加工せずそのまま次の段へ流します。`=>` を省略した `|: (条件)` は、条件に合う要素だけを残します（絞り込み）。
4. 動作: `drop`（その要素を捨てる）、または入力と出力が同じ型で1件ずつ処理する標準フィルター（初期対象は文字列の tolower、toupper、ltrim、rtrim、trim、camelcase、pascalcase、snakecase、quote_meta、urlencode、urldecode、htmlspecialchars、htmlspecialchars_decode、html_entity_decode、strip_tags）。入力を溜めるフィルター（sort、join、hash等）、型を変えるフィルター、ユーザー定義filterは初期範囲で拒否します。
5. 順序: 初期範囲の動作はすべて1件ずつ処理するため、出力は入力順を保ちます。
6. 条件式: 要素自身 `.`、構造体フィールド `.Field`、リテラル（int、decimal、float、char、string、bool）、enumメンバー、比較（`==` `!=` `<` `<=` `>` `>=`）、`and` / `or` / `not`、括弧。関数呼び出し・変数参照・代入は不可で、副作用はありません。比較の両辺は同じ型とし、型の不一致・存在しないフィールド・boolやenumの大小比較はコンパイルエラーです。比較規則は決定0054の型ごとの規則に従います。
7. 実行: コンパイラーが条件を型検査済みの式木として引数（JSON）に変換し、VMの組み込みフィルター番号-44で評価します。既存のフィルター番号とバイトコードは変更しません。bounded pipeの背圧と入力closeの伝播は既存フィルターと同じです。

## 受入テスト（実装時）

排他判定、else有無、`|: (条件)` の絞り込み、`drop`、要素自身とフィールドの条件、全リテラル型とenum、and/or/notと括弧、入力順の保持、空入力、bounded pipe、AloeBC round-trip後の実行。拒否境界として、溜めるフィルター・型変換フィルター・ユーザー定義filterの指定、型不一致、存在しないフィールド、関数呼び出し、`else` の後の分岐。両TFM全テスト、Release、英日の標準フィルター仕様の4.1書き換え。

## 後続

入力を溜めるフィルターを動作に使う場合の順序規則、分岐ごとの出力先への振り分け、複数フィルターの連結を動作にすること。
