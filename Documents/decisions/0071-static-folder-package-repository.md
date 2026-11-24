# 0071 Static folder package repository

Status: Accepted (option A)
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user response「A」
Implementation status: Not implemented. Product acceptance requires `.local/package-repository-<date>/acceptance.json`.
Rubric: P08 (External registry protocol and metadata retrieval). Publisher trust/signatures (former P11) are deferred to the HTTPS extension decision.

## 問題

ビルドツール仕様は `aloe restore` / `aloe install` の `--source <url-or-path>` と `aloe pack` を定義していますが、取得元に何がどの形式で置かれるか、`pack` のアーカイブの形式、信頼の扱い、複数の取得元の優先順位は未定義でした。

## 決定

1. リポジトリは静的なファイル置き場です。第1段階ではフォルダー（ローカル・ファイル共有）だけに対応します。`--source` にURLを指定した場合は「未対応」のエラーです。
2. リポジトリの構成

   ```
   <repo>/
     aloe-repo.json
     <name>/<version>/<name>-<version>.aloepkg
   ```

   - `aloe-repo.json`（索引）: `formatVersion: 1` と、各パッケージの `name`、`version`、`file`（リポジトリからの相対パス、区切りは `/`）、`sha256`（アーカイブのSHA-256、小文字16進）、`dependencies`（名前と `versionRange`。`.meta.json` の写し）の配列。名前と版の組は一意です。
   - 版の書き方は決定0031の3つの数字、範囲の書き方は決定0070に従います。
3. アーカイブ（`.aloepkg`）は `aloe pack` の出力で、ZIP形式の中に `package.abc` と `package.meta.json` を含みます（任意で `README` 等の文書）。ZIPの付加情報（日時・権限）は決定0057と同じく固定し、同じ入力から同じアーカイブを作ります。展開後の合計サイズ上限は既定64 MiBです。
4. 信頼（第1段階）: フォルダーを管理する人を信頼する前提とします。取得したアーカイブのSHA-256を索引の値と照合し、一致しなければエラーにして使いません（改ざん・破損の検出）。アーカイブ内の `package.meta.json` の名前・版・依存関係が索引と一致することも検証します。署名と公開者の信頼は、HTTPS対応の決定で扱います。
5. 複数の取得元: `--source` を複数指定した場合は指定順に探し、最初に見つかったものを使います。同じ名前・版が複数の取得元にあり、SHA-256が異なる場合はエラーです（取り違えの防止）。
6. 公開: `aloe repo add --repo <folder> <package.aloepkg>` が、アーカイブを所定の場所へコピーし、索引を作り直します。既存の同じ名前・版をハッシュの異なるアーカイブで上書きすることは拒否します（同じハッシュなら何もしません）。アップロード、認証、検索APIは設けません。
7. 索引の書き換えは、一時ファイルに完全に書いてから置き換える方式とし、書き込み途中の索引を他の利用者が読まないようにします。
8. 取得したものの検証済みの保存（キャッシュ）と、`packages-dir`・`package-db.json` への反映はP09・P10で決めます。

## 将来のHTTPS対応（本決定の範囲外）

同じ構成をHTTPSの静的な配信に置けるようにします。その際、索引の署名と公開者の信頼の方針を別の決定で定めます。

## 受入テスト（実装時）

索引とアーカイブの形式、`pack` の決定性、`repo add` による登録・同一ハッシュの再登録・異なるハッシュの上書き拒否、索引の置き換えの安全性、SHA-256不一致と `package.meta.json` 不一致のエラー、複数の取得元の順序とハッシュ不一致のエラー、URL指定の未対応エラー、展開サイズ上限。両TFM全テスト、Release、英日のパッケージ仕様とビルドツール仕様の追記。
