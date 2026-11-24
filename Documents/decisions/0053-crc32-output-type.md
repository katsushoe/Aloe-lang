# 0053 Standard filter crc32: output type

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「推奨案は自動採用で進める (Recommended)」 (standing approval to adopt the recommended option for small standard-filter/library contracts)
Implementation status: Not implemented. Product acceptance requires `.local/crc32-20261122/acceptance.json`.
Rubric: F19 (CRC32 output-type contract and implementation)

## 問題と現在の仕様

標準フィルター仕様（`Documents/{en,ja}/Aloe-standard-filters.html` 5.2.2）は `filter crc32 : pipe<byte> -> pipe<uint32>` と定義し、「入力全体のCRC32値を1つの整数として出力する」としています。しかし現行言語には `uint32` 型がありません（実装済みの数値型は int(符号付き32bit)、byte、char、float、decimal）。このため出力型を決めない限り実装できません。

既存の同系統フィルターは次のとおりです。

| フィルター | 入力 | 出力 |
| --- | --- | --- |
| md4 / sha1 / sha256 | `pipe<byte>` | `pipe<byte>`（ダイジェストのbyte列） |
| bin2hex | `pipe<byte>` | `pipe<string>`（大文字16進1件） |

## 選択肢

| 案 | 出力 | 長所 | 短所 |
| --- | --- | --- | --- |
| A（推奨） | `pipe<byte>`：CRC32値をbig-endianの4byteで出力 | md4/sha1/sha256と同じ形で、`|: crc32 |: bin2hex` により一般的な表記（例 `CBF43926`）が得られる。新しい型・opcode・AloeBC変更が不要 | 仕様の「1つの整数」から「4byte」へ変更になる |
| B | `pipe<decimal>`：0〜4294967295の値を1件 | 値を正確に1つの数値で表せる | decimalは計算用の型で、ハッシュ値の受け渡しには不自然。16進表記へ変換する標準フィルターがない |
| C | `pipe<int>`：32bitをそのまま符号付きint32として1件 | 1つの整数で軽量 | 2^31以上の値が負数になり、表示値が一般的なCRC32と一致しない |
| D | `uint32` 型を言語へ追加 | 仕様の記述どおり | 型システム・演算・AloeBC・WASM・仕様全体に及ぶ大きな変更で、F19の範囲を超える |

## 推奨する契約（案A）

- シグネチャ: `filter crc32 : pipe<byte> -> pipe<byte>`、名前空間 `aloe.crypto.hash`、短縮構文 `source |: crc32 | destination;`。
- アルゴリズム: CRC-32/ISO-HDLC（多項式 0x04C11DB7、反転入出力、初期値・最終XOR 0xFFFFFFFF）。zlib/PNG/ZIP と同じ一般的なCRC32です。
- 入力全体を消費し、入力のcloseを受けて4byte（big-endian）を出力してから出力pipeをcloseします。空入力では `00 00 00 00` を出力します。
- 入力値がbyte以外の場合はVMエラーとし、既存ハッシュフィルターと同じ境界を持たせます。入出力pipeの型が一致しない接続はコンパイルエラーです。
- 既存の協調スケジューラ上でストリーミング計算し、bounded pipeでも入力を保持し続けません。

## 受入テスト（採用後）

- 既知値: `"123456789"` → `CBF43926`、空入力 → `00000000`、1byte、チャンク境界をまたぐ長い入力。
- `|: crc32 |: bin2hex` の連結、bounded pipe、AloeBC round-trip後の実行。
- 型不一致（`pipe<string>`入力、`pipe<string>`出力）のコンパイル拒否、byte以外の値のVMエラー。
- 両TFMの全テスト、Release、英日仕様の更新とHTML検証。

## 対象外

- `uint32` 型の導入（案D）、CRC32以外のCRC方式、WASM直接出力でのpipe/filter（W16）。
