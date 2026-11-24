# 0069 Static state follows the Object state model (the type is an Object)

Status: Accepted (option B; replaces the earlier option A' of the same day)
Date: 2026-11-23 JST
Adopted: 2026-11-23 JST, user responses「A案だと…デバッグフラグのようなものを持てないのでは？ System.isDebug = true」「よく考えるとB案の方がプログラマーが理解すべき知識が少なくて済むと思った」
Implementation status: Not implemented. Product acceptance requires `.local/static-state-<date>/acceptance.json`.
Rubric: L39 (Remaining readonly-field, setter and extended static-member contracts) — the static-state part. Remaining readonly kinds are implementation work (reclassified 2026-11-23).
Revision note: the earlier option A' ("out-of-Tick settings" writable only from main) is withdrawn. The file name is kept for traceability.

## 問題

言語仕様の未解決事項に、可変のグローバル変数・可変のstaticフィールド・setter付きpublic static propertyの裏側の状態をどこまで許すか、VMThreadでの同期をどう定めるかが挙げられていました。

## 決定

型のstatic状態は「その型という1つのObjectの状態」として扱い、Objectのフィールドと同じ規則に従います。プログラマはObjectの規則だけを理解すればよく、static専用の規則を設けません。

```aloe
class Display {
    static field windowMode: bool = false;

    public static property WindowMode: bool { get { return windowMode; } set { windowMode = value; } }
    public static async method toggle(): void { windowMode = not windowMode; }
}

class Game {
    public async method onKey(key: Key): void {
        if (key == Key.F11) { Display.toggle(); }             // 変更の依頼（そのTick内で処理）
    }
    public async method render(): void {
        if (Display.WindowMode) { ... }                        // 確定済みの値を読む
    }
}

function main(args: string[]): int {
    Display.WindowMode = true;                                 // setterでの書き込み
    while (true) { tick(); }
}
```

1. 宣言: `static field 名前: 型 = 初期値;`。型の初期化はmainの実行前に宣言順で1回だけ行います。`static readonly field` は初期化後に変更できません。
2. 所有者: staticフィールドの所有者はその型です。直接読み書きできるのは、その型のstaticメソッド（同期・async）とstatic propertyのgetter / setterだけです。他の型やインスタンスのコードからは、public static property（読み取り・setter）とpublic static asyncメソッドを通じてアクセスします。Objectのフィールドとpublic property / public asyncメソッドの関係と同じです。
3. 状態の見え方（Objectと同じ）:
   - Tickの処理中の書き込みは確定前の状態（Volatile State）を更新し、Tickの終了時に確定状態（Committed State）へ反映されます。
   - その型自身のコードは最新の確定前の値を、他のコード（public static propertyのgetter経由）は確定済みの値を読みます。
   - Tick全体が失敗した場合は、他のObjectと同じく確定前の状態を捨てます（決定0065）。
   - Tickの外（main）での書き込みは、Objectの場合と同じく直ちに確定状態へ反映されます。
4. 同時実行: 同じ型のstatic状態に対する書き込み（staticメソッド・static setterの実行）は、その型について1つずつ順に実行されます（決定0065の「同じ型へのstatic呼び出しは順に1つずつ」と同じ保証）。異なる型どうしは並列に実行され得ます。
5. public static propertyのsetter（仕様7.1.2）は、インスタンスのsetter（決定0047）と同じ同期のsetterとして残します。
6. トップレベルのグローバル変数は設けません（状態は必ずいずれかの型またはObjectに属します）。トップレベルには `const` だけを許します。
7. 実装: staticフィールドの領域（確定前・確定済み）、型の初期化、型ごとの書き込みの直列化をVMに追加します。既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません（staticフィールドの表は既存のメタデータ領域の拡張として扱い、詳細は実装時に定めます）。

## 受入テスト（実装時）

staticフィールドの宣言・初期化順・static readonly、型自身からの直接の読み書きと他の型からの直接アクセスのコンパイルエラー、public static property（getter / setter）とpublic static asyncメソッドによるアクセス、Tick中の確定前の値と確定済みの値の見え方、Tick終了時の確定、Tick失敗時の巻き戻し、Tickの外での書き込み、同じ型への同時書き込みの直列化、トップレベル変数の拒否、AloeBC round-trip。両TFM全テスト、Release、英日の言語仕様7.1.2と未解決事項の更新。
