# Aloe Programming Language


> 生成 AI と一緒に設計している、小さくて強い型付けの「パイプ指向」言語。
> ライセンス: MIT


Aloe は、静的型付き・シンプルな構文・スタックベースの VM を持つ、
**実験的なプログラミング言語**です。特に **pipe** と **filter** による
I/O とデータ変換を第一級機能として設計しています。


現在のフォーカス:


- 読みやすく、仕様として明示された **言語仕様**
- **WebAssembly と対応しやすい** AloeVM バイトコード形式（`AloeBC`）
- テキスト処理・REST 呼び出し・ストリーミングなどを気持ちよく書ける **pipe/filter モデル**
- Object ownership / CallBuffer / Volatile→Committed / Tick による **race-free マルチスレッド実行モデル**


> ⚠️ Aloe は実験的な言語であり、実運用向けではありません。
> 構文・VM・ツールチェーンは今後も互換性のない変更が入る可能性があります。


---


## ドキュメント


- Aloe 言語仕様書（HTML）
  https://stk2k.github.io/Aloe-lang/Documents/ja/Aloe-lang-spec.html
- AloeVM 仕様書（HTML）
  https://stk2k.github.io/Aloe-lang/Documents/ja/Aloe-vm-spec.html
- AloeVM オペコード仕様書（HTML）
  https://stk2k.github.io/Aloe-lang/Documents/ja/Aloe-vm-opcode.html
- Aloe 標準フィルタライブラリ (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/ja/Aloe-standard-filters.html
- Aloe 標準ライブラリ仕様書 (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/ja/Aloe-stdlib-spec.html
- Aloe パッケージ仕様書 (HTML)
  https://stk2k.github.io/Aloe-lang/Documents/ja/Aloe-package-spec.html


---


## 特徴（設計方針）


### 静的型付け + 型推論


```aloe
var n = 42;      // int に推論
var pi = 3.14;   // float に推論
let name: string = "Aloe";
```


- `var` によるローカルな型推論
- `let` による明示的な型指定


### 値型 / 参照型の明確な区別


- `struct` … 値型（値コピー）
- `class` … 参照型（ヒープ確保）
- `delete obj;` … 参照を `null` にする専用構文
  （ソースコード上では `obj = null;` は書けない）


### シンプルで明示的な制御構造


- すべての文末に `;` が必須
- インデントに意味はない（Python 的な構文ではない）
- `try / catch / finally` による例外処理


---


## pipe / filter による I/O とデータフロー


言語レベルでパイプラインを記述できます。


```aloe
main(args: string[]) {
    let lines: pipe<string> = pipe<string>.create();


    stdin
      | filter(utf8)       // byte -> string
      | filter(lineSplit)  // string -> string（1 行ごと）
      | lines;


    foreach (line in lines) {
        if (line == "") { break; }
        print("LINE: " + line);
    }


    _ = 0; // 終了コード
}
```


### filter の定義


```aloe
filter lineSplit {
    in:  pipe<string>;
    out: pipe<string>;


    bound(input, output) {
        foreach (chunk in input) {
            let lines = chunk.split("\n");
            foreach (line in lines) {
                output.write(line);
            }
        }
        output.close();
    }
}
```


- `in` / `out` による入出力の型宣言
- `bound(input, output)` が実際の変換処理
- パイプライン中では `filter(lineSplit)` として利用


---


## trait + with 構文によるミックスインスタイル


```aloe
trait Printable {
    method printSelf(): void {
        print(this.toString());
    }
}


class User {
    private name: string;


    construct(name: string) {
        this.name = name;
    }


    public property Name: string {
        get { return name; }
    }


    private method toString(): string {
        return "User(" + name + ")";
    }
}


main(args: string[]) {
    var u = new User("alice");


    var p = u with Printable;
    p.printSelf();   // "User(alice)"


    _ = 0;
}
```


- trait はメソッドを持つ再利用可能な部品
- `with` を使ってインスタンスに付与（ミックスイン的イメージ）


---


## AloeVM & AloeBC の概要


- スタックベースの VM を想定
- コンパクトなバイトコード形式 `AloeBC`
  - 先頭にマジック `"ALOEBC"` とバージョンバイト（例: Major/Minor/Build）
- 命令セットは **WebAssembly と 1:1 に近い対応**を意識して設計


## Object / Tick / CallBuffer VM（設計ドラフト）


Aloe の並列実行モデルでは、**Mutable State の所有単位を class Object** とします。
外部 Object は class member variable を直接 Read / Write せず、状態取得は `public property`、状態変更要求は `public async method` を使います。


主な原則:


- Object 自身だけが自身の member variable を変更する
- `public property` は外部から Committed State を読む同期 Query
- public property setter は存在しない
- 外部から呼べる public instance method は `public async method ... : void`
- `async` は C# の async/await ではなく、対象 Object の **CallBuffer への enqueue**
- self call (`targetObject == currentObject`) は同期実行
- private / protected method、通常 function、通常 static method は同期実行できる
- `public static async method` は type ごとの **Static CallBuffer** を使う
- Tick 中の Object 内部更新は **Volatile State**
- 外部 Object から見えるのは Tick 終了まで **Committed State**
- Tick 終了時に `Volatile → Committed`
- 従来の `TaskChangeBuffer` / Change Record 方式は廃止
- Tick 中に新たに enqueue された Call も同 Tick 内で処理する
- Instance / Static CallBuffer が空、実行中 async method が 0、Scheduler pending work が 0 になった状態を **Tick Quiescence** とする
- `tick()` は main から呼ぶ同期 Barrier
- VM Tick と Render Frame / VSYNC は別概念
- CallBuffer は race-free を保証するが、lock取得順に依存するため現時点では完全 Replay Determinism を保証しない


### 状態可視性


```text
Committed hp = 100
Volatile hp  = 70


Object 自身:
    hp -> 70


外部 Object:
    player.HP -> 100
```


### CallBuffer


```text
Caller
   |
   v
Target Object CallBuffer
   |
   v
Owner Object async method
   |
   v
Volatile State
   |
   v
Tick Commit
```


### Tick


```text
main()
  |
  | Root / Tick Driver
  v
tick()
  |
  +-- Instance CallBuffer execution
  +-- Static CallBuffer execution
  +-- newly generated Calls
  |
  v
Tick Quiescence
  |
  v
Volatile -> Committed
  |
  v
Reference Graph / Reference Group
  |
  v
Reference Count / GC
  |
  v
return to main()
```


Object Reference の寿命管理は **Reference Counting** を基本とし、循環参照は Object Reference Graph の SCC を **Reference Group** として扱います。
Reference Count 0 の Object / Group は即 free せず Destruction Candidate とし、CallBuffer entry・VM Stack・Volatile Reference 等の Runtime Temporary Reference を考慮した Tick 終了処理で安全に破棄します。


---
### WASM フレンドリーな設計


- AloeVM の命令を WebAssembly のオペコードにできるだけ素直にマップ
- 長期的な理想:


  - 開発フロー:
    `Aloe ソース → WASM → 実行環境（ブラウザ / サーバーなど）`
  - `AloeVM` インタプリタはデバッグ・非 WASM 環境向け


---


## サンプル: シンプルな REST 風パイプライン


HTTP クライアント + JSON フィルタを使ってユーザ情報を 1 件取得する
イメージのコードです（標準ライブラリの具体 API はまだ検討中のため擬似例です）。


```aloe
struct UserInfo {
    field id:    int;
    field name:  string;
    field email: string;
}


main(args: string[]) {
    let request:  pipe<byte>    = pipe<byte>.create();
    let response: pipe<byte>    = pipe<byte>.create();
    let users:    pipe<UserInfo> = pipe<UserInfo>.create();


    // 擬似コード: httpClient が request/response を使って HTTP を実行
    httpClient("GET", "https://api.example.com/users/1", request, response);


    response
        | filter(utf8)
        | filter(json<UserInfo>)
        | users;


    // 「現在のパイプから先頭 1 件を取得」するイメージのメソッド
    let u = users.next();


    if (u is UserInfo) {
        print("User: " + u.name + " <" + u.email + ">");
    }


    _ = 0;
}
```


※ `httpClient` や `json<T>` のオプション指定など、
標準フィルタ / ライブラリの詳細は別ドキュメントで設計中です。


---


## プロジェクト構成（案）


リポジトリ構成の一例（実際には変更される可能性があります）:


```text
.
├─ docs/
│  ├─ aloe-lang-spec-ja.md      # Aloe 言語仕様書（日本語）
│  ├─ aloe-lang-spec-en.md      # Aloe 言語仕様書（英語）
│  ├─ aloe-vm-spec-en.md        # AloeVM / AloeBC / GC / WASM マッピング仕様
│  └─ filters-standard-lib.md   # 標準フィルタ仕様書
├─ src/
│  ├─ compiler/                 # Aloe → AloeBC コンパイラ（C#, WIP）
│  ├─ vm/                       # ネイティブ AloeVM インタプリタ（C#, WIP）
│  └─ aloe2wasm/                # Aloe → WASM バックエンド（WIP）
├─ examples/
│  ├─ hello-world.aloe
│  ├─ pipes/                    # pipe + filter サンプル
│  └─ web/                      # 簡単な REST / Web 風サンプル
├─ tests/
│  └─ ...
├─ LICENSE
└─ README.md
```


現時点では、`docs/` 以下の仕様書が「仕様のソース・オブ・トゥルース」です。


---


## ステータス


- ✅ **言語設計**
  型システム / 制御構造 / pipe/filter / trait / 例外など、かなり細かいところまでドラフトあり
- ✅ **VM 設計**
  スタックモデル / バイトコード `AloeBC` / GC 方針 / WASM 対応に加え、Object ownership / CallBuffer / Volatile・Committed State / Tick Quiescence / Reference Group の実行モデルまでドラフトあり
- ⏳ **実装**
  コンパイラ / VM / WASM バックエンドは開発中 or 構想段階
- ⏳ **ツールチェーン**
  REPL / フォーマッタ / LSP / パッケージマネージャなどは将来の課題


実験的な言語・ランタイム設計に興味がある方からの Issue / PR / 議論を歓迎します。


---


## 貢献の仕方の例


**実装面**


- コンパイラの一部（パーサ / 型チェック / コード生成など）の試作
- 簡易な AloeVM インタプリタの実装
- 最小サブセット向けの `aloe2wasm` バックエンドのプロトタイプ


**仕様の改善**


- 曖昧な部分やエッジケースの洗い出しと提案
- 代替構文や別のセマンティクス案（できれば具体例付き）


**サンプル & テスト**


- pipe/filter を活かしたテキストツール
- 簡単な REST / Web 風フローのサンプル
- trait + `with` のパターン集


大きな設計変更を提案する場合は、PR の前に Issue や Discussion で方針を共有してもらえると、
言語全体の一貫性を保ちやすくなります。


---


## ロードマップ（ざっくり）


### 言語


- 0.x 系として一旦の構文・型システムを「凍結」する
- 標準フィルタライブラリ仕様を固める
- コレクションや標準型の詳細仕様を拡充する


### VM


- 命令セット（オペコード）と AloeBC バイナリ形式の確定
- CallBuffer Scheduler / VMThread Pool / Tick Quiescence の実装
- Volatile / Committed State と Tick Commit の実装
- Reference Counting / Reference Group / Destruction Candidate の実装
- 簡易 GC を含むリファレンス C# VM の実装


### WASM


- 「コアサブセット」向け Aloe → WASM マッピングの実装
- インタプリタを介さない直接 WASM 実行のプロトタイプ


### ツール


- CLI ツール（例: `aloec`, `aloevm`）
- フォーマッタ / シンタックスハイライト
- 簡易なパッケージ管理の仕組み（最初はシンプルなもので良い）


---


## ライセンス


Aloe は **MIT License** で公開される予定です。
詳細はリポジトリ内の `LICENSE` ファイルを参照してください。