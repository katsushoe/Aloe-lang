# パッケージ一覧

[English](PACKAGES.md) | [日本語](PACKAGES.ja.md)

この文書にはNuGetの直接参照を記録します。プロジェクト間参照はソース依存関係のため一覧に含めません。厳密なパッケージバージョンの正本は`Directory.Packages.props`、参照とメタデータの正本は各プロジェクトファイルです。2026-10-20に解決済みバージョンを変更せず中央管理へ移行し、Solutionと別管理のWiXでlocked restore・オンライン依存監査が成功しました。

## 直接参照パッケージ

| パッケージ | バージョン | 参照プロジェクト | 用途・メタデータ |
| :--- | :--- | :--- | :--- |
| `Microsoft.AspNetCore.Components.WebAssembly` | `10.0.13` | `AloeRepl.Wasm` | Blazor WebAssemblyの実行・ビルド統合。 |
| `Microsoft.AspNetCore.Components.WebAssembly.DevServer` | `10.0.13` | `AloeRepl.Wasm` | ローカル開発サーバー。`PrivateAssets="all"`。 |
| `coverlet.collector` | `6.0.4` | `Test_CompilerLib` | テストカバレッジ収集。テストプロジェクト内でのみ使用。 |
| `Microsoft.NET.Test.Sdk` | `18.0.1` | `Test_CompilerLib` | .NETテスト実行基盤。 |
| `NUnit` | `4.4.0` | `Test_CompilerLib` | 単体テストフレームワーク。 |
| `NUnit.Analyzers` | `4.11.2` | `Test_CompilerLib` | NUnit診断。テストプロジェクト内でのみ使用。 |
| `NUnit3TestAdapter` | `5.2.0` | `Test_CompilerLib` | NUnit用VSTestアダプター。 |
| `WixToolset.Util.wixext` | `7.0.0` | `Installer/Aloe.Installer.wixproj` | WiXユーティリティ拡張。更新後はMSI成果物を再検証します。 |

## 更新ルール

- 直接参照の追加・削除、バージョンまたはメタデータの変更と同じ作業で、この一覧も更新します。
- `ProjectReference` などのプロジェクト間参照は第三者パッケージではなく、各`.csproj`を正本とします。
- パッケージソースと認証情報は実行中のNuGet設定に委ねます。この文書には認証情報や端末固有のソース設定を保存しません。
- 採用済みの[設計判断0022](Documents/decisions/0022-package-and-dependency-management.md)に従い、厳密な中央バージョン指定、推移的依存のpinning無効、テスト・Browser REPL・インストーラーの`packages.lock.json`のバージョン管理を行います。WiX SDKの版はプロジェクト宣言に残します。
- CI・リリースの依存検証には`./scripts/Test-Dependencies.ps1`を実行します。SolutionとWiXを別々にlocked restore・オンライン監査し、JSON結果を`.local/dependency-audit`へ保存します。監査ソース取得失敗とHigh/Critical検出は失敗扱いです。Moderateはリリース前に対応判断を記録し、Lowは保守時に確認します。
- .NET保守更新時および最低四半期ごとに更新を確認します。中央バージョンとlock fileを同時更新し、ビルド・テスト・影響するホストとMSIを検証します。CIプロバイダーへの接続と自動更新サービスは担当決定後に導入します。
- Browserプロジェクトは`RuntimeFrameworkVersion=10.0.13`を宣言し、通常のlocked restoreとRelease publishのSDK依存を一致させます。暗黙のASP.NET内部assets、ILLink tasks、WebAssembly packは10.0.13でlockします。インストール済みEmscripten workload toolsの版とは別です。

