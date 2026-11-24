# 0061 AES filters limited to AES-256-GCM; Blowfish removed

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「A」 (including removal of Blowfish)
Implementation status: Not implemented. Product acceptance requires `.local/aes-gcm-<date>/acceptance.json`.
Rubric: F20 (AES public context/mode contract and implementation); F21 (Blowfish public context/mode contract and implementation) is closed by removal from the specification.

## 問題

標準フィルター仕様5.4.2は `AESencrypt(ctx: AESContext)` / `AESdecrypt(ctx: AESContext)` と「CBC / GCMなど、モードとパディングはAESContextで定める」とだけ記述し、`AESContext` の内容、ノンス管理、出力形式、鍵の渡し方、復号失敗時の扱いが未定義でした。Blowfishも同様に `BlowfishContext` が未定義でした。

## 決定（AES）

1. 方式はAES-256-GCMだけです。CBC、ECB、CTRなど他のモードと、鍵長128/192ビットは提供しません。`AESContext` 型は廃止します。
2. 構文: `source |: AESencrypt: key | sealed;` と `sealed |: AESdecrypt: key | plain;`。`key` は長さ32の `byte[]` の変数（ローカル変数・引数・const以外の式で得た値）です。このためフィルター引数に、文字列リテラル以外として「byte[]型の変数」を渡せるよう文法を拡張します（このフィルターでだけ許可）。鍵の長さが32でない場合は実行時のVMエラーです。
3. ノンス: 暗号化のたびにVMが暗号学的乱数（`System.Security.Cryptography.RandomNumberGenerator`）で96ビットのノンスを生成します。利用者がノンスを指定する方法は提供しません。
4. 出力形式: `0x01`（形式の版）+ ノンス12バイト + 暗号文 + タグ16バイト。追加認証データは使いません（空）。
5. 暗号化・復号とも入力closeまでバッファし、変換後に出力してcloseします。復号はタグの検証が成功した後にだけ平文を出力します。形式の版が違う、長さが足りない、タグ不一致（改ざん・鍵違い）の場合は、平文を1バイトも出力せずにVMエラーとし、エラーメッセージに鍵・平文・暗号文の内容を含めません。
6. 平文の最大長は64 MiBとし、超えた場合はVMエラーです（バッファのメモリ上限）。
7. 鍵の後始末: VMは使用後に内部の鍵コピーをゼロで上書きしますが、言語の `byte[]` 自体はGCで移動・複製され得るため、メモリから確実に消えることは保証しません（仕様に明記）。ソースに鍵を直接書くとAloeBCに残る旨も警告として記載します。
8. ブラウザー版（browser-wasm）では.NETのAES-GCMが使えないため、実行時に「この環境では未対応」のVMエラーとします。WASM直接出力は対象外です（W16）。
9. 実装は.NET標準の `AesGcm` を使います。組み込みフィルター番号を末尾に追加し、既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません。

## 決定（Blowfish）

Blowfishは64ビットのブロック長で大量データに対する既知の弱点（Sweet32）があり、.NET標準にも実装がないため、`BlowfishEncrypt` / `BlowfishDecrypt` と `BlowfishContext` を標準フィルター仕様から削除します。F21は「削除による仕様の確定」として扱い、受入には英日仕様からの削除と、名前を使った場合に未知のフィルターとしてコンパイルエラーになるテストを用います。

## 受入テスト（実装時）

往復（空・1バイト・大きな入力・bounded pipe）、毎回異なるノンスで同じ平文から異なる出力になること、出力形式の長さ、外部（.NETで別途作成）の暗号文の復号、改ざん・鍵違い・版違い・短すぎる入力で平文を出さずにエラーになること、32バイト以外の鍵の拒否、文字列リテラルや他の型の鍵のコンパイル拒否、64 MiB上限、AloeBC round-trip、Blowfish名のコンパイル拒否。両TFM全テスト、Release、英日の標準フィルター仕様5.4の書き換え。

## 後続

パスワードからの鍵生成（PBKDF2等）、追加認証データ、大きなデータ向けの分割形式、鍵を扱う専用の型。
