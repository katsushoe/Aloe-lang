# AloeSample 実行サンプル（AloeVM 最小実装デモ）

このディレクトリには、AloeVM の最小実装と、それを使った **バイトコード実行サンプル** が含まれています。  
ここでは `AloeSample/Program.cs` の内容に沿って、日本語の説明をまとめます。

## サンプルの概要

`Program.cs` では、次のような疑似コードに相当する処理を、  
**手書きバイトコード** + **簡易ビルダ (`BytecodeBuilder`)** で構成し、AloeVM 上で実行しています。

```csharp
int i = 0;
while (i < 5)
{
    print(i);
    i = i + 1;
}
print("Done");
```

実行結果は次のようになります。

```text
0
1
2
3
4
Done
```

## 構成ファイル

- `Aloe.Runtime` 名前空間
  - `AloeVm` : VM 本体（`switch (Opcode)` で命令を実装）
  - `Opcode` : VM の命令種別（`Add`, `Sub`, `Jump`, `JumpIfFalse` など）
  - `AloeValue` / `ValueKind` : VM 上で扱う値の表現（int / bool / string / null）
  - `OperandStack` : オペランドスタック
  - `Module` / `FunctionInfo` / `CallFrame` : バイトコードモジュールとコールフレーム
  - `BytecodeReader` : バイトコードから `byte` / `int32` を読み出すユーティリティ

- `AloeSample/Program.cs`
  - `Main` メソッド
    - 定数テーブル（`List<AloeValue>`）を構築
    - `BytecodeBuilder` を使って、while ループ + `print` 処理のバイトコードを構築
    - `FunctionInfo` と `Module` を組み立て、`AloeVm` に渡して実行
  - `BytecodeBuilder`
    - `Emit(Opcode)` / `EmitInt32(int)` でバイト列を積み上げる
    - `MarkLabel("name")` でラベル位置を記録
    - `EmitJump(Opcode, "label")` でジャンプ命令を出力しつつ、後でオフセットをパッチ
    - `ToArray()` 呼び出し時にラベルを解決し、相対ジャンプオフセットを書き込む

## バイトコードの流れ（ざっくり）

1. 定数テーブルを準備する
   - `const[0] = 0`  （初期値 i）
   - `const[1] = 5`  （ループ終了条件）
   - `const[2] = 1`  （インクリメント値）
   - `const[3] = "Done"` （最後に表示する文字列）

2. ローカル変数
   - `local[0] = i` として 1 個だけ確保

3. バイトコードで表現している処理

   - `i = 0;`
     - `PushConst 0` → `StoreLocal 0`

   - ラベル `loop_start` をマーク

   - ループ条件 `i < 5`
     - `LoadLocal 0`
     - `PushConst 1`
     - `CmpLt`
     - `JumpIfFalse loop_end`

   - ループ本体
     - `LoadLocal 0`
     - `Print`
     - `LoadLocal 0`
     - `PushConst 2`
     - `Add`
     - `StoreLocal 0`

   - `Jump loop_start`

   - ラベル `loop_end` をマーク

   - ループ終了後の処理
     - `PushConst 3`
     - `Print`
     - `Halt`

4. `BytecodeBuilder.ToArray()` 呼び出し時に、
   - `loop_start` / `loop_end` の位置をもとに、
   - `Jump` / `JumpIfFalse` の相対オフセットを自動的にパッチする。

## ビルドと実行方法（例）

1. プロジェクトを .NET コンソールアプリとして作成し、`Aloe.Runtime` 名前空間のソースと `AloeSample/Program.cs` を同じソリューションに追加します。
2. ターゲットフレームワークは `.NET 8.0` などを想定しています。
3. ビルド後、コンソールから実行すると、次の出力が得られます。

```text
0
1
2
3
4
Done
```

## 今後の拡張イメージ

このサンプルはあくまで **「AloeVM 最小コア + while ループのテスト」** を目的としたものです。  
今後は次のような拡張を想定しています。

- 追加の Opcode（比較・論理演算・関数呼び出しなど）の実装
- Aloe 言語のフロントエンドコンパイラから、この VM 向けバイトコードを自動生成
- デバッグ用トレース出力（IP / スタック内容など）
- 単体テストプロジェクトを追加し、Opcode ごとの挙動を検証

まずはこのサンプルを足がかりに、AloeVM の命令セットと実行モデルを少しずつ広げていく想定です。
