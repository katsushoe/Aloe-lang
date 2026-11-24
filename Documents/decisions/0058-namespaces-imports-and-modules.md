# 0058 Namespaces, imports and multi-file module resolution

Status: Accepted (option A, two phases)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「A」
Implementation status: Not implemented. Phase acceptance requires `.local/modules-phase1-<date>/acceptance.json` and `.local/modules-phase2-<date>/acceptance.json`.
Rubric: L35 (Source namespace/import and multifile module resolution); prerequisite for H07 and H10.

## 問題

言語仕様第17章（Modules / namespace / import）は本文がなく、名前空間の宣言、importの形、名前解決の順序、ファイルの探索、コンパイル単位、公開範囲が未定義でした。標準ライブラリ仕様は `import aloe.log.LogLevel;` を前提にしています。

## 決定

1. 名前空間はファイル単位で宣言します: `namespace app.util;`（ファイル先頭、1ファイルに最大1つ）。省略したファイルは無名の既定名前空間に属します。名前空間名は小文字のASCII英数字と`_`を`.`でつないだ形です。
2. importは3形式です。
   - 1記号: `import aloe.log.log;`
   - 一括: `import aloe.log.*;`（その名前空間の直下の公開記号。下位の名前空間は含みません）
   - 別名: `import aloe.log.LogLevel as Level;`
   importはnamespace宣言の後、他の宣言の前に置きます。
3. 名前解決の順序: ローカル（引数・変数）→ 現在の名前空間 → 1記号・別名のimport → 一括import → 暗黙の `aloe` / `aloe.system`。同じ段階で同名が複数見つかった場合はコンパイルエラーです（曖昧さを推測で解決しません）。完全修飾名 `a.b.Name` はimportなしで常に使えます。
4. ファイルの探索: 名前空間とフォルダーを対応させます（`app.util` → `<source root>/app/util/*.aloe`）。CLIは入口ファイルとソースのルート（既定は入口ファイルのフォルダー）から、importされた名前空間のファイルを探します。決定0031のローカル依存パッケージのソースも同じ規則で、パッケージごとのルートから探します。見つからない名前空間はコンパイルエラーです。
5. コンパイル単位: 到達したすべてのファイルを1つのAloeBCにまとめてコンパイルします（全体コンパイル）。AloeBC形式とローダーは変更しません。名前空間間の循環importは許可します。
6. 公開範囲: トップレベル宣言は既定で公開です。`internal` を付けた宣言は同じパッケージ（同じソースのルート）からだけ参照できます。既存のメンバー単位の public / private / protected は変更しません。
7. 標準ライブラリ `aloe.*` はコンパイラーに組み込んだ記号表として提供し、ファイル探索の対象にしません（H07の基盤）。
8. 既存の単一ファイルのプログラムは、namespace・importなしで従来どおりコンパイルできることを保証します。REPLは入力ごとに既定名前空間として扱い、REPL内のimportは第1段階では標準ライブラリだけを許可します。WASM直接出力は全体コンパイル後のモジュールを従来どおり扱います。

## 実装の段階

- 第1段階: 1ファイル内の `namespace` 宣言、完全修飾名、標準ライブラリ `aloe.*` のimport（3形式）、名前解決の順序と曖昧さのエラー、REPLでの標準ライブラリimport。
- 第2段階: 複数ファイルの探索と全体コンパイル、利用者の名前空間のimport、`internal`、循環import、ローカル依存パッケージのソース、CLIのソースのルート指定。

## 受入テスト（実装時）

各import形式、別名、一括importの範囲（下位の名前空間を含まない）、解決順序、曖昧さ・存在しない記号・存在しない名前空間・import位置の誤りのコンパイルエラー、完全修飾名、既存の単一ファイルプログラムの互換性。第2段階では複数ファイル・循環import・`internal`違反・パッケージのソース・AloeBC round-trip、CLIとREPLの動作。両TFM全テスト、Release、英日の言語仕様第17章の本文作成。

## 後続

ファイルごとの分割コンパイルと読み込み時の結合（AloeBC形式の変更を伴う）、名前空間の再エクスポート、条件付きimport。
