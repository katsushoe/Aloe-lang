# 0062 Traits as stateless compile-time mixins

Status: Accepted (option A)
Date: 2026-11-22 JST
Adopted: 2026-11-22 JST, user response「A」
Implementation status: Implemented (2026-11-24). Classes apply traits after the existing `extends` / `implements` clauses (`class Name [extends Base] [implements I] with T1, T2`); the `:` base syntax in the example below is not part of the language. Tests: `Test_CompilerLib/TraitTests.cs`. Product acceptance: `.local/traits-20261124/acceptance.json`.
Rubric: L33 (Trait declarations, composition and type checks). Related: L34 (interface default members), decision 0060 (`__TRAIT__`).

## 問題

言語仕様7.5は「設計の下書きのみ」で、traitに書けるもの、適用の構文、衝突の解決、型としての扱い、interfaceとの役割分担が未定義でした。

## 決定

1. 宣言:

   ```aloe
   trait Greeter {
       require method name(): string;
       method greet(): void { print("Hello, " + name()); }
       public async method wave(): void { greet(); }
   }

   class Player : Actor with Greeter, Scorer {
       private method name(): string { return "player"; }
   }
   ```

2. traitに書けるもの: 本体付きのメソッド（同期メソッド、public async メソッド。公開範囲の指定は通常のクラスメソッドと同じ）と、適用先のクラスに実装を求める `require method 名前(引数): 型;`。フィールド、コンストラクター、プロパティ、staticメンバーは持ちません（可変の状態はクラスだけが持つ原則を維持）。traitのメソッドは、クラスのフィールドへ直接アクセスできず、`require` したメソッドや他のtraitメソッドを通じて操作します。
3. 適用: `class 名前 [: 基底クラス] with T1, T2, ...`。適用対象は当面クラスだけで、struct・exception・enumには適用できません。traitが他のtraitを `with` で含むこと（trait同士の合成）は可能で、循環はコンパイルエラーです。
4. 展開: コンパイル時に、traitのメソッドを適用先クラスのメソッドとして複製します。VM・AloeBC・syscallの変更はありません。asyncメソッドの呼び出し関係・循環検査は、展開後のクラスに対して既存の規則で行います。
5. 衝突の解決: 適用したtrait同士（基底クラスのメソッドを含む）で同名・同じ引数のメソッドが衝突した場合はコンパイルエラーです。適用先クラス自身が同名のメソッドを定義した場合はそれを優先し、衝突しません。基底クラスとの関係は既存の継承・override規則に従います。
6. `require`: 適用先クラス（基底クラスからの継承を含む）が同じ名前・引数・戻り値型のメソッドを持たない場合はコンパイルエラーです。
7. 型としての扱い: trait名はinterfaceと同様に型として使え、変数・引数・戻り値・配列要素の型にできます。traitを適用したクラス（およびその派生クラス）の値はtrait型へ代入でき、`as` / `is` 相当の照合（既存のinterfaceのcast・照合と同じ仕組み）で判定できます。trait型の値から呼べるのは、そのtraitの公開メソッドだけです。
8. interfaceとの役割分担: interfaceは実装を持たない型の取り決め、traitは実装を持ち込める部品で型としても使える、と仕様に明記します。L34（interfaceの既定実装）はtraitと重複しない範囲で別途決定します。
9. `__TRAIT__`（決定0060）は、traitのメソッド本体内で、そのtraitの完全修飾名になります（展開後も適用先クラス名にはなりません）。

## 受入テスト（実装時）

宣言・適用・複数trait・trait同士の合成、requireの充足と不足、クラスによる優先、衝突エラー、フィールド等の禁止、async メソッドの展開と循環検査、trait型への代入・引数・配列・cast・照合、trait型から非公開メソッドを呼べないこと、継承との組み合わせ、struct等への適用拒否、循環合成の拒否、`__TRAIT__`、AloeBC round-trip。両TFM全テスト、Release、英日の言語仕様7.5の書き換え。

## 後続

traitのフィールド（状態）、structへの適用、メソッドの別名・除外による衝突解決、traitのstaticメンバー。
