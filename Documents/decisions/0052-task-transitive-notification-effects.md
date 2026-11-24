# 0052 Task protected regions: transitive notification analysis

Status: Accepted
Date: 2026-11-21 JST
Adopted: 2026-11-21 JST, user response「提案0052を採用（推奨）」
Implementation status: Compiler implementation present; product acceptance requires `.local/task-effects-20261121/acceptance.json`.
Prerequisite: adopted0051 initial Task subset; accepted local source4844 per TFM.

## 問題と現在の仕様

0051の初回受入は、Taskのtry/catch/finallyからの直接の非同期呼び出しを拒否する範囲です。同期ヘルパー内部で通知が発生するかは検査しません。これは未実装の拡張であり、初回受入範囲への回帰ではありません。

検証済みSDK4844のCompilerLibを参照するコンパイル専用プローブで、以下を確認しました。VM実行結果は検証していません。

| 呼び出し | 現在のコンパイル結果 |
| --- | --- |
| Taskのtryから直接Objectのasyncメソッドを呼ぶ | 拒否 |
| 同期の自由関数内から通知する | 受理 |
| Task自身の同期呼び出し先から通知する | 受理 |
| 同期コンストラクタ内から通知する | 受理 |
| 引数評価中の同期関数内から通知する | 受理 |
| 通知しない同期関数を呼ぶ | 受理 |

Evidence: `.local/task-effects-investigation-20261121/Program.cs`, `report.json`.

## 推奨する範囲

Taskのtry/catch/finallyから実行される**ソース言語内の同期呼び出し**について、推移的な通知の可能性を検査します。既存のObject・CallBuffer・Volatile・例外伝播・Tickの実行方式は変更しません。Task以外のtryの新しい制約は、この提案に含めません。

内部解析は次の3状態とし、ソース構文や公開バイトコードへ属性を追加しません。

- NoAsyncNotification: 検査対象のソース内に通知を発生させる呼び出しがない。
- MayNotify: Objectのinstance/static async呼び出し、または外部Taskへの通知に到達し得る。
- Unknown: 呼び出し先やTask自身であることを確定できない。

NoAsyncNotificationは、例外・I/O・状態変更がないという意味ではありません。同期例外は既存どおり捕捉でき、汎用ロールバックは導入しません。

Taskの保護領域ではMayNotifyとUnknownをコンパイルエラーにし、呼び出し位置と到達する通知先、または不確定な呼び出しを診断します。条件分岐を実行可能性で除外しない保守的な解析とします。

## 実装計画

1. 全FunctionSignatureのindexを頂点として、意味解決済みの同期呼び出し辺と通知発生箇所を収集します。自由関数・同期static/instanceメソッド・Task自身のメソッド・constructor・getter/setter・structメソッド・引数評価中の呼び出しを漏らさず対象にします。
2. `ParseResolvedFunctionCall`だけへ追加して済ませず、既存の`CALL`発行箇所を列挙し、constructor等の専用経路も含めます。TaskConstruct/TaskInvokeは専用syscallなので、意味解析時の型・呼び出し先・self証明を使って辺を記録します。
3. Task自身への呼び出しは、既存のthis／安定したself alias証明を利用します。start/stop/deleteの同期ライフサイクル操作を、外部Taskメソッドへの通知と混同しません。新しいTaskのconstructorは、そのTask自身の文脈で解析します。
4. 全関数の解析完了後にSCCまたは固定点で状態を伝播し、ソース順や前方参照に依存させません。通知のない同期再帰を、再帰という理由だけで拒否しません。
5. Taskのtry/catch/finally内の各呼び出し位置を検証します。未知のdispatchを既知の同期処理と推測しません。動的な同期dispatchを完全に解決できない場合は、その保護領域の呼び出しを拒否します。
6. 既存の`ValidateAsyncCallGraph`はasyncメソッド間の循環検査として維持します。現在の`_asyncCallEdges`は通常の同期関数やconstructorを網羅しないため、今回の解析へ流用しません。

主な変更候補: `CompilerLib/AloeCompiler.cs`, `CompilerLib/AloeCompiler.Tasks.cs`, 新しいpartialファイル。実装時には解決済みCALL発行経路を再確認して対象を確定します。RuntimeLib・バイトコード形式・既存の通知順序を変更する案ではありません。

## 受入テスト

- 上記6件を製品回帰テストへ移し、間接通知4件が拒否へ変わることを確認します。
- 複数段ヘルパー、前方参照、通知を含む再帰、通知しない再帰を確認します。
- Taskのrun/constructor/通知メソッドのtry/catch/finallyを確認します。
- getter/setter/同期static/structメソッドと、引数評価を通る通知の拒否を確認します。
- this／安定self aliasの同期呼び出し、外部Task通知、不確定receiverの境界を確認します。
- 保護領域外で許可されている通知と通常Objectプログラムが維持されることを確認します。
- ソースの解析順を変えても診断の可否が変わらないことを確認します。
- 両TFM全テスト、Release13、仕様の両言語文書を検証します。

## 後続へ残すもの

- ホストcallbackが任意にVMへ再入し、別の処理を登録する挙動の静的証明。
- 外部モジュール／Task module replacement／動的ロードを含む完全なeffect契約。
- 通知先の処理結果・非同期例外を呼び出し元へ返す仕組み。
- 汎用rollback、Object Pool、CallBufferバッチ化、Task並列実行。

## 採用と実装の区別

0051では推移的検査を後続とし、上記の間接通知を含むプログラムは現在コンパイルできます。2026-11-21にユーザーご本人が本提案を採用しました。上記の受入可否の変更は承認済みで、再承認は不要です。製品実装と検証が完了するまでは、採用済み・未実装として扱います。

## 実装上の境界（2026-11-21）

通常の同期CALLに加え、getter syscall・Task生成・Task self呼出しを意味解決済みの辺として収集します。動的pipe callbackとTickで実行される外部登録処理はUnknownです。Tickのmain限定は変更しません。検証済みであることは受入記録で判定し、本文の実装記述だけでは受入を主張しません。
