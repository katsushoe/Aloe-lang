# 0057 Standard filters zip / unzip / tar / untar: single-entry archive model

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「推奨案ですすめて」 (option A, decompressed-size limit 64 MiB)
Implementation status: Not implemented (after 0055/0056). Product acceptance requires `.local/archive-filters-<date>/acceptance.json`.
Rubric: F18 (ZIP/TAR archive filter contracts and execution)

## 問題

標準フィルター仕様7.1は `zip` / `unzip` / `tar : pipe<byte> -> pipe<byte>` と「ストリーム全体を1つのアーカイブとして扱う簡易モデル」だけを定義し、エントリ名・日時・権限、複数エントリ入力、tarの逆変換、展開サイズの上限が未定義でした。

## 決定

1. 4つのフィルターを名前空間 `aloe.archive` に定義します。すべて `pipe<byte> -> pipe<byte>` です。
   - `zip`: 入力全体を1エントリのZIP（Deflate）にします。
   - `unzip`: 1エントリのZIPを展開して内容を出力します。
   - `tar`: 入力全体を1エントリのTAR（POSIX ustar）にします。
   - `untar`: 1エントリのTARから内容を出力します（新規追加）。
2. 付加情報は固定し、同じ入力から常に同じ出力を得ます。エントリ名 `data`、更新日時 1980-01-01 00:00:00（ZIPの最小日時。TARも同値）、TARの権限 0644・所有者0、コメントや拡張フィールドなし。
3. `unzip` / `untar` は、エントリがちょうど1つで、通常ファイルのアーカイブだけを受け付けます。エントリ数0または2以上、ディレクトリやリンクのエントリ、暗号化ZIP、壊れたアーカイブ、未対応の圧縮方式はVMエラーです。エントリ名は検査せず、ファイルシステムへの書き出しもしません。
4. 展開後のサイズ上限は既定で64 MiB（67,108,864 byte）です。超えた時点で出力を止めてVMエラーにします。圧縮前の入力サイズの上限は既存フィルター同様、上限を設けず入力数に比例してメモリを使います（信頼できない入力の隔離保証ではありません）。
5. 入力closeまでバッファし、変換後にbounded pipeの背圧に従って出力し、出力をcloseします。空入力は `zip` / `tar` では空ファイル1エントリのアーカイブ、`unzip` / `untar` ではVMエラーです。
6. 実装は.NET標準の `System.IO.Compression` と `System.Formats.Tar` を使い、新しいVM命令やバイトコード形式の変更は行いません。

## 受入テスト（実装時）

zip→unzip、tar→untarの往復（空・1byte・大きな入力・bounded pipe）、出力の決定性（同じ入力で同一byte列）、既知の外部生成アーカイブの展開、64 MiB上限の超過エラー、複数エントリ・ディレクトリ・壊れたデータ・暗号化ZIPの拒否、型不一致のコンパイル拒否、AloeBC round-trip。両TFM全テスト、Release、英日の標準フィルター仕様7.1の書き換え。

## 後続

複数エントリ・ファイル名を扱う上位API、gzip / gunzip（圧縮専用フィルター）、上限値の設定方法。
