# 0063 Interface property contracts; defaults via traits

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「A」
Implementation status: Implemented (2026-11-24). Items 1, 2, 3 and 5: syscall 64 `InterfacePropertyDispatch`, `Test_CompilerLib/InterfacePropertyContractTests.cs` and the English/Japanese language specification 7.4. Item 4 (trait methods satisfying interface contracts) arrived with decision 0062: `Test_CompilerLib/TraitTests.cs`. Product acceptance: `.local/traits-20261124/acceptance.json` (shared with decision 0062).
Rubric: L34 (Interface default/synchronous/value-returning member contracts). Related: 0018 (interface async dispatch), 0047 (property setters), 0062 (traits), L40 (value-returning async).

## 問題

決定0018のinterfaceは `void` を返す public async メソッドの取り決めだけを宣言できます。既定の実装、同期メンバー、値を返すメンバーの扱いが未定義でした。言語は、他のObjectとのやり取りを「public property（同期の問い合わせ）」と「public async メソッド（Tickで処理される変更依頼）」に限定しています。

## 決定

1. interfaceにプロパティの取り決めを追加します。

   ```aloe
   interface Scored {
       property Score: int { get; }
       property Name: string { get; set; }
       public async method reset(): void;
   }
   ```

   `get;` だけの取り決めは、getterを持つ公開プロパティで満たされます。`get; set;` の取り決めは、決定0047のsetterを持つ公開スカラープロパティで満たされます。型は完全一致です。継承したプロパティも取り決めを満たします。interfaceの継承（`extends`）では、プロパティの取り決めもメソッドと同じ規則で平坦化・合成し、矛盾する型や読み書きの違いはコンパイルエラーです。
2. 意味: interface型の値からの読み取りは、実体のクラスのgetterを実行し、既存のプロパティ問い合わせと同じく確定済み（Committed）の状態を読みます。書き込みは実体のクラスのsetterを、既存の同期setterと同じ規則で実行します。getter・setterをTickに積むことはありません。
3. 値を返す同期メンバーはプロパティだけとし、interfaceに同期メソッド（値の有無を問わず）は追加しません。値を返す async メソッドはL40で扱います。
4. 既定の実装はinterfaceに持たせません。実装の共有はtrait（決定0062）で行い、`class P implements Scored with ScoreDefaults` のように、traitのメソッドでinterfaceの取り決めを満たせます。この役割分担（interfaceは型の取り決め、traitは実装を持ち込む部品）を仕様に明記します。
5. 実行時の呼び分け: 既存のasync呼び分けと同様に、コンパイル時にinterfaceへ代入可能な既知のクラスとそのgetter / setterの対応表を作り、実体の型で選ぶsyscallを末尾に追加します。既存のsyscall番号・opcode・AloeBCコンテナ形式は変更しません。interface自身のオブジェクトや実行時のinterface表は作りません。
6. クラスのプロパティのoverride（現在未対応）は本決定に含めませんが、同じ呼び分けの仕組みを後続で利用できます。

## 受入テスト（実装時）

getのみ・get/setの取り決めの充足と不足、型不一致・読み書き不一致の拒否、継承したプロパティによる充足、interfaceの継承と合成、interface型の値からの読み取り（Committedの読み取りを含む）と書き込み、派生クラス、配列・コレクション要素からの読み取り、traitによるメソッドの取り決めの充足、interfaceでの同期メソッド・本体付きメソッドの拒否、AloeBC round-trip。両TFM全テスト、Release、英日の言語仕様7.4の更新。

## 後続

クラスのプロパティのoverride、値を返す async メソッド（L40）、interfaceのstaticメンバー。
