# 0067 Host APIs are static synchronous methods

Status: Accepted
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user responses「ホストAPIはstaticメソッドであり、CallBufferを利用するasyncとは別の仕組み。そもそもstaticメソッドはTickの中で実行されるのでは？」「だからAudio.playを実行した際の例外はAudio.playを呼び出したコンテキストでcatchできる。」「「ホスト側の実装を同時に呼んでも安全にするには」はそのオブジェクトの性質の問題であり、言語仕様とは別」
Implementation status: Specification change; current host intrinsics (readLine, sleep) already follow this model. Product acceptance requires `.local/host-apis-<date>/acceptance.json` (regression tests + bilingual spec update).
Rubric: H12 (Standard Host query/command and side-effect atomicity contracts). Amends: 0066 (host recoverable errors).

## 問題

標準ライブラリ仕様第7章は、ホストへの同期の問い合わせのスレッド安全性（7.3）、ホストへの命令をpublic static asyncとして表す場合の直列化と実行スレッド（7.4）、Tick失敗時のホストの副作用の扱い（7.5）を未解決としていました。

## 決定

1. ホストAPI（`Keyboard.isDown`、`Audio.play`、`Network.send` などのホストが実装する機能）は、static同期メソッドです。CallBufferを使わず、呼び出した場所でその場で実行され、値を返すものは値を返します。言語上の扱い（呼び方・型検査・戻り値）は、利用者が書くstatic同期メソッド（決定0045）と同じです。
2. 実行される時点は呼び出し元によって決まります。asyncメソッドの中（Tickの処理中）から呼べばTickの中で、mainから呼べばTickの外で実行されます。
3. ホストAPIで起きた例外は通常の同期の例外で、呼び出した場所のtry/catchで捕捉できます。捕捉されなかった場合は、asyncメソッドの中なら決定0065の順序（クラス内の `on error` → `CallBuffer.onError` → Tick全体の失敗）、mainの中ならmainの外へ伝わります。
4. ホストAPIの副作用（音・通信・ファイル・描画など）は、Tickが失敗しても巻き戻されません（`print` と同じ）。失敗したTickの副作用を避けたい場合は、Tickの中では状態だけを変え、`tick()` が正常に戻った後でmainからホストAPIを呼ぶ書き方を仕様に例示します。言語として実行を遅らせる仕組みは設けません。
5. 同時に呼ばれても安全かどうかは各ホストAPIの実装の性質であり、言語仕様の対象外です。各ホストAPIの説明に記載します。VMはホストAPIの呼び出しを直列化しません。
6. 標準ライブラリ仕様7.4の「ホストへの命令をpublic static asyncメソッドとして表す」記述を削除し、7.3・7.5の未解決事項は本決定で解消したと記載します。権限（AloeHostCapability）による利用可否の制御は現行どおりです。

## 決定0066の修正

0066の「ホストAPIの回復可能なエラー（Error / Warn）で `set_error_handler` を呼ぶ」場面を削除します。ホストAPIのエラーは通常の例外として3.の経路で扱い、mainから抜けた場合だけ0066の `Fatal` の場面に該当します。

## 受入（実施するもの）

現行のホスト機能（readLine、sleep）について、Tick内・Tick外での即時実行、例外の呼び出し元での捕捉、async内で未捕捉の場合の0065の経路、Tick失敗で副作用が巻き戻されないこと、権限拒否時の例外を回帰テストで確認します。英日の標準ライブラリ仕様第7章を更新します。新しいVM機能・syscall・AloeBC変更はありません。
