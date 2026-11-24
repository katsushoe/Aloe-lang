# 0060 Standard global constants and magic constants

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「A」
Implementation status: Not implemented. Product acceptance requires `.local/global-constants-<date>/acceptance.json`.
Rubric: H08 (Source version/range/path/magic global constants)

## 問題

標準ライブラリ仕様第3章は、importなしで参照できる `aloe` 名前空間の定数を列挙していますが、値を決める時点が混在していました。AloeBCは別の環境で実行できるため、実行環境に依存する値をコンパイル時に埋め込むと誤った値になり、ビルドしたPCのパスも配布物に残ります。

## 決定

1. 値を決める時点で2種類に分けます。
   - コンパイル時に確定し、リテラルとして埋め込む定数:
     `ALOE_VERSION` / `ALOE_MAJOR_VERSION` / `ALOE_MINOR_VERSION` / `ALOE_RELEASE_VERSION`（コンパイラーの版）、`ALOE_DEBUG`（Debug構成でコンパイルした場合true）、`ALOE_INT_MIN` = -2147483648、`ALOE_INT_MAX` = 2147483647、`ALOE_FLOAT_MIN` = 1.17549435E-38（binary32の最小の正の正規化数）、`ALOE_FLOAT_MAX` = 3.4028235E+38、`ALOE_EOL` = "\n"、位置・文脈の定数。
   - 実行時に実行中のVM（ホスト）から取得する定数:
     `ALOE_OS`（"Windows"、"BSD"、"Darwin"、"Solaris"、"Linux"、"Unknown"）、`ALOE_TARGET`（"x86"、"x64"、"arm"、"arm64"、"wasm"）、`ALOE_BINDIR`、`ALOE_PLUGINDIR`、`ALOE_DIR_SEPARATOR`、`ALOE_MAX_PATHLEN`。
   `ALOE_OS` と `ALOE_TARGET` は仕様の「ビルド時」から「実行時」へ変更します。
2. 位置・文脈の定数:
   - `__FILE__`: ソースのルート（決定0058）からの相対パスで、区切りは常に `/`。REPLでは `<repl>`、標準入力からのコンパイルでは `<stdin>`。絶対パスは埋め込みません。
   - `__DIR__`: `__FILE__` のフォルダー部分。ない場合は `.`。
   - `__LINE__`: その記述がある行番号（1から）。
   - `__FUNCTION__`: 現在の関数・メソッドの単純名。関数外ではコンパイルエラー。
   - `__METHOD__`: 名前空間（0058）と型名を含む完全修飾名（例: `app.util.Parser.parse`）。関数外ではコンパイルエラー。
   - `__CLASS__`: 現在のclass / struct / enum / exceptionの完全修飾名。それらの宣言の外ではコンパイルエラー。
   - `__TRAIT__`: traitの実装（L33）まで使用するとコンパイルエラー。
3. 定数の型は仕様どおり（string、int、float、bool）で、constの初期化式や他の式で通常の値として使えます。実行時に取得する定数は、各定数の最初の参照時にVMから取得し、同じ実行の間は同じ値です。
4. `ALOE_` で始まる名前と `__名前__` の形の名前は予約し、利用者が変数・関数・型などに使うとコンパイルエラーにします。
5. WASM直接出力: コンパイル時の定数は同じ値、`ALOE_TARGET` は "wasm"、`ALOE_OS` は "Unknown"、パス系の文字列定数は空文字列、`ALOE_MAX_PATHLEN` は0とします。
6. 実装: 実行時の定数を取得するsyscallを1つ末尾に追加します。既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません。ホストの権限（ファイルアクセス等）は追加しません。

## 受入テスト（実装時）

全定数の型と値、constでの利用、実行時定数が実行環境の値になること（AloeBCを別プロセスで実行）、`__FILE__` が相対パスで絶対パスを含まないこと、REPL・標準入力での値、各文脈定数の位置による値とエラー、`__TRAIT__` の拒否、予約名の拒否、WASMでの値、AloeBC round-trip。両TFM全テスト、Release、英日の標準ライブラリ仕様第3章の更新。

## 後続

`ALOE_MAX_PATHLEN` を使うファイルAPI、traitの実装後の `__TRAIT__`、ビルド構成の名前を表す定数。
