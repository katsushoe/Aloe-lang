# Windows MSI

WiX 7プロジェクトから、x64・全ユーザー向けMSIを生成します。対象はCLIコンパイラー、VMホスト、AloeからWASMへのコンパイラー、Native REPL、サンプルです。各アプリは`win-x64`自己完結形式でpublishし、WebアプリであるBrowser REPLは含めません。Windows Installerのライフサイクル受け入れが完了するまではプロトタイプです。

## ビルド

リポジトリルートから実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Installer/BuildMsi.ps1 -ProductVersion 0.1.0
```

各ホストをpublishし、`.local/installer/staging`に配置してMSIをビルドした後、スクリプトが識別用マーカーを確認して作業領域を削除します。MSIの出力先は`.local/installer/out/Aloe-lang-<version>-x64.msi`です。NuGet復元は既定で構成済みソースを使います。必要パッケージがすべてキャッシュ済みなら`-OfflineRestore`で現在のユーザーのNuGetキャッシュから復元できます。

ランタイムの既定版は`10.0.13`です。インストール済みSDKが既定で選ぶパッチ版には依存しません。別の.NET 10パッチ版を使う場合は`-RuntimeFrameworkVersion <10.0.patch>`を指定します。復元とpublishに同じ版を指定し、各アプリの生成済みランタイム構成がその版と一致することを確認してからMSIへ収録します。

初版`0.1.0`のProductCodeは`{0FDC5E40-27FB-4891-997D-255CA29F47E6}`、UpgradeCodeは`{60A2445F-3183-4842-9170-3C9E72A75C93}`です。以降のmajor upgradeでは、より大きい3フィールド版数と新しいProductCodeが必要です。PackageCodeはビルドごとにWiXが生成します。

`-SkipMsiValidation`は、Windows Installer ICE検証を実行できないビルド環境向けです。MSIの検証やインストール受け入れを満たすものではありません。インストール、upgrade、downgrade、repair、uninstall、環境変数優先順位、ACL、ファイルハッシュ、データ保持の試験は、破棄可能なWindows VMで行ってください。

ビルド後、`powershell -NoProfile -ExecutionPolicy Bypass -File Installer/Test-MsiArtifact.ps1 -MsiPath .local/installer/out/Aloe-lang-0.1.0-x64.msi`を実行すると、MSIをdecompileし、識別情報、アーキテクチャ、ホストと設定ファイル、書込み可能ディレクトリのACL、環境変数の設定を検査します。この静的検査はICE検証やVMでのライフサイクル受け入れの代わりにはなりません。

既定のインストール先は64bit Program Filesです。既存のmachine `ALOE_INSTALL_ROOT`は保持し、未設定またはAloeが所有する場合だけmachine値を設定します。ユーザー単位の環境変数は通常のWindows優先順位で適用されます。標準Usersグループの変更権限は`logs`と`data`だけに与え、uninstall後も空でないユーザーデータを残します。
## 使い捨てVM用ライフサイクル実行スクリプト

`Test-MsiLifecycle.ps1`は初期MSIと、版・ProductCode・PackageCodeを更新したmajor upgrade用MSIを受け取ります。Aloe製品系列・x64/per-machine・hashを読み取り専用で確認し、既定では5段階の計画と`report.json`だけを出力します。

```powershell
pwsh -NoProfile -File Installer/Test-MsiLifecycle.ps1 `
  -InitialMsi C:\VmTest\Aloe-lang-0.1.0-x64.msi `
  -UpgradeMsi C:\VmTest\Aloe-lang-0.1.1-x64.msi `
  -ReportDirectory C:\VmTest\reports-plan
```

実実行は専用の使い捨てx64 Windows VMへ両MSIとスクリプトをコピーし、クリーンなsnapshotを取得してから行います。VM内の管理者権限付き64bit PowerShellで、上の引数に`-Execute -AcknowledgeDisposableVm -DisposableVmName <対象VM名>`を追加してください。VM名は`COMPUTERNAME`との一致が必須で、仮想マシンとして識別されることも確認します。既存のAloeインストール先・製品系列の登録・machine/user環境変数・同名レポート先がある場合は、基準ケースの実行を拒否します。

初期インストール後にpayload hashとlogs/dataの確認用ファイルを保存し、インストール済みCLI実行ファイル1個だけを削除して完全修復を検証します。その後、更新・旧版拒否・削除を行い、製品登録、修復後のhash、旧版拒否の終了コードと版エラーのログ、所有ファイルの削除、確認用ファイルの保持、MSI所有のmachine環境変数の作成・削除を確認します。各操作は詳細MSIログを残します。再起動が必要な結果は未完了として停止します。失敗時は調査用にVM状態とレポートを残すため、新しい基準ケースの前にsnapshotへ戻してください。

この基準ケースが成功しても、標準ユーザーによる実効ACL・書き込み確認、および既存のmachine/per-user環境変数の優先順位検証は完了しません。別のクリーンなsnapshotで実行して証跡を残す必要があります。ビルド・静的検査・実操作をモックしたスクリプト契約検査は、実際のMSIライフサイクル成功を示しません。

### 追加証跡と標準ユーザーの検証

実行スクリプトは、初期・更新後のSHA-256一覧を`report.json`へ保存し、削除後に製品系列、Aloe所有マーカー、両版のuninstallキーを64bit/32bitレジストリビューで確認します。これは対象のインストーラー登録に限定した検査であり、全レジストリやWindows Installerキャッシュの走査ではありません。

各実行にはクリーンな使い捨てVMのsnapshotを使ってください。以前のGUI削除でユーザーファイルが残ったインストール先は意図的に拒否します。検証のためだけにそのデータを削除せず、snapshotへ戻す前に過去のレポートを退避してください。両MSIと2本のスクリプトを、インストール先以外のゲスト内作業フォルダーへコピーします。

標準ユーザーの検証には、Administratorsグループに属さないアカウントが必要です。専用VM内での作成・選択はご本人が行ってください。スクリプトはアカウント作成やグループ変更を行いません。管理者側のライフサイクル実行に`-PauseForUserAcceptance`を追加すると、初期インストール後、修復前に一時停止します。標準アカウントへ切り替え、管理者権限を付けない64bit PowerShellで実行します。

```powershell
powershell -NoProfile -File C:\VmTest\Test-MsiUserAccess.ps1 `
  -Execute -AcknowledgeDisposableVm -DisposableVmName $env:COMPUTERNAME `
  -CheckUserOverride
```

`Test-MsiUserAccess.ps1`の既定動作は書き込みを伴わない計画表示です。実行時は昇格済み・制限付きの管理者トークンを拒否します。`logs`/`data`内で一意名のファイルを作成・読込・変更・改名・削除し、`bin`/`config`での作成がアクセス拒否になることを確認します。ACLと既存ユーザーファイルは変更しません。JSONレポートの既定出力先は標準ユーザーのローカルアプリデータ配下の`AloeMsiValidation`です。表示された出力先を控え、管理者セッションへ戻ってEnterを押すとライフサイクルを続行します。両レポートを合わせて判定し、一時停止しただけではACL検証成功としません。

`-CheckUserOverride`は一意のユーザー単位`ALOE_INSTALL_ROOT`を一時設定し、Windowsの`CreateEnvironmentBlock`で現在のシェルの古い環境を継承しない新しい環境ブロックを取得します。ユーザー値が既存machine値より優先されることを確認し、`finally`で元のユーザー値へ戻します。確認するのはログイン済みユーザーのWindows環境構成であり、Aloeアプリがその値を利用することまでは検証しません。この一時検証中にサインアウトやプロセス中断を行わないでください。[CreateEnvironmentBlockの仕様](https://learn.microsoft.com/windows/win32/api/userenv/nf-userenv-createenvironmentblock)を参照してください。

既存machine値の保持は、Aloeインストールとユーザー単位overrideのない別のクリーンなsnapshotで確認します。既存値を上書きせず、ご本人が検証用machine値を設定して、その完全一致値を管理者側の実行スクリプトへ渡します。

```powershell
if ($null -ne [Environment]::GetEnvironmentVariable('ALOE_INSTALL_ROOT', 'Machine')) {
  throw 'Use a clean snapshot without a machine override.'
}
$expectedRoot = Join-Path $env:ProgramData 'Aloe-existing-root'
[Environment]::SetEnvironmentVariable('ALOE_INSTALL_ROOT', $expectedRoot, 'Machine')
powershell -NoProfile -File C:\VmTest\Test-MsiLifecycle.ps1 `
  -InitialMsi C:\VmTest\Aloe-lang-0.1.0-x64.msi `
  -UpgradeMsi C:\VmTest\Aloe-lang-0.1.1-x64.msi `
  -ReportDirectory C:\VmTest\reports-existing-override `
  -Execute -AcknowledgeDisposableVm -DisposableVmName $env:COMPUTERNAME `
  -ExpectedMachineOverride $expectedRoot
```

実行スクリプトはこの指定値を作成・削除しません。初期値の完全一致と、インストール・修復・更新・削除後の保持を確認します。レポートを回収したら使い捨てsnapshotへ戻してください。実インストールは`-Execute`とVMの明示指定がある場合だけ実行します。ホストでの計画・構文検査はVM受け入れの証拠にはなりません。

コマンドとAutomationの根拠：[msiexec](https://learn.microsoft.com/windows-server/administration/windows-commands/msiexec)、[読み取り専用database mode](https://learn.microsoft.com/windows/win32/msi/installer-opendatabase)、[製品系列の列挙](https://learn.microsoft.com/windows/win32/msi/installer-relatedproducts)。

