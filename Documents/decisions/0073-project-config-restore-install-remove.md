# 0073 Project configuration, restore / install / remove and revalidation

Status: Accepted (option A)
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user response「A」
Implementation status: Not implemented (after 0070-0072). Product acceptance requires `.local/package-install-<date>/acceptance.json`.
Rubric: P10 (Dependency plan application/install/uninstall and revalidation). Also defines the project configuration file format, which no specification defined.

## 問題

ビルドツール仕様は `aloe restore` / `aloe install` とオプションだけを示し、プロジェクト設定ファイル（例: `aloe.config.json`）の形式、導入先、ロックファイルの扱い、削除、再検証、失敗時の状態は未定義でした。

## 決定

1. プロジェクト設定ファイル `aloe.config.json`（プロジェクトのルートに置く）

   ```json
   {
     "formatVersion": 1,
     "name": "myapp.core",
     "version": "0.1.0",
     "sources": ["//fileserver/aloe-repo", "C:/aloe-local-repo"],
     "dependencies": [
       { "name": "aloe.text", "versionRange": "1.x" },
       { "name": "myapp.util", "versionRange": ">=0.2.0 <0.3.0" }
     ]
   }
   ```

   - `name` / `version` は決定0031の規則、`versionRange` は決定0070の書き方です。
   - `sources` は取得元のフォルダーの一覧で、決定0071の順序規則に従います。コマンドの `--source` を指定した場合はそちらが優先されます。
   - 不明なキーはエラーにします（書き間違いの見落としを防ぐため）。ビルド設定の項目は、ビルドツール仕様の決定で追加します。
2. 導入先: プロジェクト内の `.aloe/packages/<name>/<version>/package.abc` と `package.meta.json`、およびプロジェクト用の `.aloe/package-db.json`。`--packages-dir` で導入先を変更できます。
3. `aloe restore`
   - 設定を読み、`aloe.lock.json` があればその版を検証して使い、なければ決定0070で解決します。
   - 必要なアーカイブを決定0072のキャッシュ経由で取得し、導入先へ展開して `package-db.json` を更新します（`--no-package-db-update` で更新しない）。
   - ロックファイルがなかった場合は自動で作成します（決定0046の形式）。
   - 既に導入済みでハッシュが一致するものは展開し直しません。
4. `aloe install <name>[@<range>]`: 設定ファイルの `dependencies` に追加（既にあれば範囲を更新）し、ロックファイルのその依存関係に関わる部分を再解決して `restore` を実行します。`@` を省略した場合は、解決した版を `^` ではなく `<major>.x` の形で記録します（決定0070の書き方の範囲内）。
5. `aloe remove <name>`: 設定ファイルから外し、再解決してロックファイルを更新し、使われなくなったパッケージを導入先から削除します。
6. `aloe update [<name>]`: ロックファイルを無視して（指定したパッケージだけ、または全体を）再解決し、ロックファイルを更新します。
7. 失敗時の状態: 展開は一時フォルダーで行い、すべて成功した場合だけ導入先・`package-db.json`・ロックファイルを一括で切り替えます。途中で失敗した場合は、前の状態のまま残ります。
8. 再検証: `aloe build` / `aloe run` は、導入済みパッケージのハッシュとロックファイルを照合し、不一致・不足の場合は「`aloe restore` を実行してください」というエラーにします。
9. 導入時にパッケージ側のスクリプトや処理は一切実行しません。パッケージはバイトコードとメタデータだけです。
10. `--global`: ツールなどをインストールルート下の共有フォルダー（`<インストールルート>\data\global-packages`）に導入する用途に限ります。プロジェクトの依存関係の解決には使いません。

## 受入テスト（実装時）

設定ファイルの形式と不明キーのエラー、restoreの初回（解決・取得・展開・ロック作成）と2回目（ロック使用・再展開なし）、install（追加・範囲の更新・省略時の記録形式）、remove（不要パッケージの削除）、update、途中失敗時の前状態の維持、build / run時の再検証エラー、`--packages-dir` / `--no-package-db-update` / `--global`、スクリプトが実行されないこと。両TFM全テスト、Release、英日のビルドツール仕様とパッケージ仕様の追記。
