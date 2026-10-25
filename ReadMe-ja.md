# Aloe Programming Language

[English](ReadMe.md) | [日本語](ReadMe-ja.md)


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
- リポジトリ文書一覧: [DOCUMENTS.ja.md](DOCUMENTS.ja.md)
- NuGet直接参照一覧: [PACKAGES.ja.md](PACKAGES.ja.md)
- セキュリティの保証範囲・制約・脆弱性報告: [SECURITY.ja.md](SECURITY.ja.md)


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
- 同期処理のcatch-all／既存例外型の順序付き型付き`try / catch` clauseと、`throw;`による再throwに対応。例外値、`finally`、async例外処理は未対応


---


## pipe / filter による I/O とデータフロー


言語レベルでパイプラインを記述できます。


```aloe
function main(args: string[]): int {
    let lines: pipe<string> = pipe<string>.create();


    stdin
      | filter(utf8)       // byte -> string
      | filter(lineSplit)  // string -> string（1 行ごと）
      | lines;


    foreach (line in lines) {
        if (line == "") { break; }
        print("LINE: " + line);
    }


    return 0; // 終了コード
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


## 提案中の trait + with ミックスイン構文

以下は設計スケッチで、実行可能なAloeコードではありません。現在のコンパイラーはtrait宣言とtraitをwithで適用する構文を実装していません。


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


function main(args: string[]): int {
    var u = new User("alice");


    var p = u with Printable;
    p.printSelf();   // "User(alice)"


    return 0;
}
```


- trait はメソッドを定義できる再利用可能な部品として検討中
- traitを`with`でインスタンスに適用する機能は未実装です。通常の`with`ブロックは別の機能です。


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


function main(args: string[]): int {
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


    return 0;
}
```


引数なしの`sort`は`pipe<int>`の入力終了まで全要素を保持し、符号付きint32の昇順で重複を保持して出力します。容量制限付きの入出力に対応し、空入力では値を出力しません。引数なしの`sort`は`pipe<string>`にも対応し、元文字列・重複を保持して、カルチャーに依存しないUTF-16序数の昇順で安定ソートします。正規化や大小文字の同一視は行いません。`sort: .Field`は宣言済みstruct直下のintまたはstringフィールドで安定昇順ソートします。intは符号付きint32、stringはUTF-16序数で比較し、元レコード・重複・文字列・同key内の入力順を保ちます。入れ子のpathと他のkey型は未対応です。 引数なしの`shuffle`も`pipe<int>`と`pipe<string>`に対応し、正規化・型変換せず元の値を入力終了まで保持し、件数・重複を保つFisher-Yatesの置換を、出力の空きを待ちながら流します。非暗号学的乱数を使い、元と同じ順序も許容します。seed・決定的再現は未対応で、メモリは入力件数に比例します。

※ `httpClient` や `json<T>` のオプション指定など、標準フィルタ / ライブラリの多くは
別ドキュメントで設計中です。`json: T`は`pipe<string>`の各項目を、必須の完全一致名を持つint / bool / stringフィールドのみのstructへ変換します。intは小数・指数表記を含まないJSON整数の字句形式を要求します。不正JSONやスキーマ不一致では項目番号・可能ならフィールド名を含むVMエラーになります。`source |: json: T | destination;`で呼び出します。現行VMはbyte列全体を厳密にUTF-8復号する `utf8`、全byte列をハッシュして32byteを出力する `sha256`、旧式互換用途に限り20byteを出力する `sha1` と16byteを出力する `md4`（どちらもセキュリティ用途には使用しないでください）と `pipe<string>` 用の引数なし `tolower`、`toupper`、`ltrim`、`rtrim`、`trim`、`lineSplit`、`camelcase`、`pascalcase`、`snakecase`、`quote_meta` と、引数付きの `startswith`、`contains`、`strstr`、`join` を実装しています。また、`pipe<byte>` に対応し、`bin2hex` はバイト列全体から大文字の16進文字列を1つ出力します。`base64encode` はbyte列全体を改行なしの標準Base64文字列へ変換します。`base64decode` は文字列全体を復号してbyte列へ戻します。 `urlencode` と `urldecode` はURI component向けRFC 3986 UTF-8 percent変換を行います。 `htmlspecialchars`と`htmlspecialchars_decode`はcanonicalな5文字のentity変換を行い、`html_entity_decode`は認識できる名前付き・数値entityを復号して未知の参照を保持します。`strip_tags`は各文字列に含まれる完結したmarkup風区間を除去しますが、HTML sanitizerではありません。`csvenode`は`pipe<string[]>`の各行をCSVへエンコードし、必要なフィールドを引用符で囲み、内部の引用符を二重化します。`csvdecode`は入力文字列ごとにCSVレコードを解析し、不正な形式ではVM例外を発生させます。`parse_str`は各入力クエリ文字列をフォーム形式（`+`を空白、`%HH`を厳密なUTF-8）でデコードし、`map<string/string>`として出力します。同一キーは最後の値を保持します。 `number_format`は`pipe<decimal>`を.NET decimal書式文字列と不変カルチャーで整形します。


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
  一部のコンパイラ／VM機能を実装し、下記ロードマップに示しています。言語全体とWASM設計は未完です。
- ⏳ **ツールチェーン**
  成功した入力間でVM／ヒープ状態を保持するサブセットREPLをネイティブ版とブラウザー版で提供しています。フォーマッタ、LSP、パッケージマネージャ、REPLの対応言語・副作用範囲の拡張は今後の課題です。


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


## テストの実行

5つの共通ライブラリと共通テストは `net8.0` と `net10.0` の両方を対象とします。.NET 10 SDKと、テスト用の.NET 8・.NET 10ランタイムを用意してください。既定のテストコマンドは両対象を実行します。片方だけを実行する場合は `--framework net8.0` または `--framework net10.0` を追加します。共通ライブラリとテストの出力は `bin/<TFM>/` に分離します。デスクトップアプリ・Browser REPL・MSIは.NET 10を維持します。公開済みpackageの識別や、利用側プロジェクトとの統合完了を示すものではありません。

`Test_CompilerLib` には `aloe2wasm` のWebAssemblyを直接実行するテストが含まれます。実行にはWebAssembly対応のNode.jsが必要です。`node` を `PATH` に追加するか、Node.js実行ファイルのパスを `ALOE_TEST_NODE` に設定して、次を実行してください。

Windowsでは、Aloeツールは既定で `E:\Aloe` をインストールルートとして使います。`ALOE_INSTALL_ROOT` に絶対パスを指定すると、config・data・logのルートを切り替えられます（隔離したローカル実行など）。

```text
dotnet test Test_CompilerLib/Test_CompilerLib.csproj
```

## ローカルパッケージ索引と依存計画

CLIは明示的なローカルのversion1 JSON索引を管理します。既存パッケージのファイル対を登録し、依存関係を解決できます。

```text
aloec package-db add --name example --version 1.0.0 --abc example.abc --meta example.meta.json --package-db package-db.json
aloec package-db list --package-db package-db.json
aloec package-db resolve --name example --version 1.0.0 --package-db package-db.json
aloec package-db remove --name example --version 1.0.0 --package-db package-db.json
```

`resolve`は各メタデータファイルを読み直して依存関係を取得します。名前は大文字小文字を区別し、要求版と依存の`versionRange`は`1.0.0`のような3区分の数値による完全一致版に限定します。各区分は`0`を除き先頭ゼロを認めません。版範囲、プレリリース版、ワイルドカード選択は未対応です。

成功時の標準出力は1つのJSON文書（`formatVersion: 1`、`root`、`packages`）です。各パッケージは依存先から順に1回だけ現れ、絶対パス`abcPath`/`metaPath`、小文字の`abcSha256`/`metaSha256`、完全一致の依存識別子を含みます。依存先を序数順にたどるため、入力が同じなら出力も同一です。パッケージ・ファイルの不足、不正なメタデータ・識別子、不対応の版、循環、同名の版競合、I/O失敗では診断と非ゼロ終了を返し、部分的な計画JSONは出力しません。

依存解決は索引・パッケージファイルを変更せず、ダウンロード・インストール・実行も行いません。ハッシュは読み取った内容の識別であり、発行元の信頼性や解決後のファイル不変性を保証しません。採用範囲と後続のregistry・版範囲・インストール・ソース連携方針は[採用済み提案0031](Documents/decisions/0031-local-exact-package-resolution-plan.md)を参照してください。

## ロードマップ（ざっくり）


### 言語


現行コンパイラーの対応範囲：固定長int[]／bool[]／byte[]／char[]／float[]／decimal[]／string[]および通常／bitfield enum・struct配列リテラル、型が不変でインスタンスを共有するclass／object配列、Object同一性比較、参照／primitive同型`as` castと失敗時だけ評価する`ifnot`代替式（数値変換なし）、型を限定しないobject／期待primitive型・演算・条件式の文脈でのboxing・非消費取り出し、名前付きboxのwith対象・型・constructor・ToXxxと不変な型のbox配列、Add／RemoveAtで変更可能なList、primitive／string／enum／名前付きclass referenceのSetリテラル・Add／Remove／Contains／Count／foreach、primitive／enum／class／object参照値を持つMapリテラル・添字読み取り・Set／Remove／ContainsKey／Count・挿入順のkey foreachとkey/value pair foreach、`void` async操作契約を持つinterface・`extends`継承・`implements`適合・interface型async dispatch・interface対象のchecked cast／bool `is`検査と両分岐が継続するif/else後とwhile条件偽の出口とcontinue後の再評価、およびリテラルtrueのwhileの到達可能なbreak経路における共通interface合流と、elseなし終了ガード・片方だけが継続するif/else後の型情報引き継ぎとAND／OR／否定の混在条件を通じた一意な共通祖先interfaceへの合流と括弧／and／or／not条件によるif／else-if／else／while内の型絞り込みと、宣言型を保つ再代入・破棄での解除と入れ子の条件分岐の合流・一度だけ実行するwith本文への型情報引き継ぎ・継続するtry/catch両経路で一意な共通祖先interfaceへ合流する型解析・同名別スコープとヒープ上の格納先を区別するwhile／foreach入口での変更候補解除とwhile条件による各反復の型情報再確立、不変型の配列／List／Set要素とMap key／valueのinterface参照、入れ子・List／配列混在リテラル／添字／Length／foreachと参照共有、VM状態を保持するサブセットREPL、同期catch-all／既存例外型の順序付き型付きcatchとbare rethrow、ローカル／引数・参照可能な参照型field／要素のdelete・期待型付き消費unbox・無効参照検査、内側参照を共有する入れ子配列、配列式の添字読み書き／struct要素フィールドへの書き戻し／Length／foreach、型付きprimitive式・関数と制御構造、asyncメソッドとgetter Queryを持つclass instance、通常／bitfield enum、primitive／enum／入れ子structフィールド、constructor・読み取り専用メソッド／getter・ローカル`with`メンバーを持つ値コピーstruct。[interface対応範囲](Documents/ja/Aloe-lang-spec.html#interface-status)・[Setの制約](Documents/ja/Aloe-lang-spec.html#set-subset)・[Mapの制約](Documents/ja/Aloe-lang-spec.html#map-subset)に記載しています。プロトタイプの実装は言語仕様全体への準拠を意味しません。

- interfaceのflow解析・defaultメンバー対応を広げ、trait合成と構造化例外を完成させてから、仕様全体の対応を検証して0.x系の構文・型システムを凍結する
- 標準フィルタライブラリ仕様を固める
- コレクションや標準型の詳細仕様を拡充する


### VM


- 命令セット（オペコード）と AloeBC バイナリ形式の確定
- CallBuffer Scheduler / VMThread Pool / Tick Quiescence の実装
- Volatile / Committed State と Tick Commit の実装
- Reference Counting / Reference Group / Destruction Candidate の実装
- 簡易 GC を含むリファレンス C# VM の実装


### WASM


直接バックエンドは、型付き`int`、`float`、`bool`、`byte`、UTF-16コード単位の`char`と構造化制御フローをサポートします。これらのスカラー型で型が変わらないidentity castはno-opとして扱います。`ifnot`によるcast失敗時の代替式は未対応です。`float`はIEEE 754 binary32へ対応し、`float`／`int`／`byte`オペランドの算術演算（`+`、`-`、`*`、`/`）と比較を行えます。混在算術はVMと同じbinary32へ昇格し、混在比較はint32の順序を保持します。非有限値、ゼロ除算、乗除算のunderflowはtrapになります。Float剰余はVMと同じbinary32で計算し、被除数の符号と符号付きゼロを保ちます。無限大の除数では有限な被除数を返し、NaNオペランドとゼロ除数はtrapになります。decimal演算と数値変換は未対応です。また、実装済みのint32算術／bitwise演算にも対応します。`byte`算術は検査付きint32へ昇格し、`byte`／`int`混在比較はintの規則を使います。export関数のint引数は符号付きint32、bool引数は0/1、byte引数は0..255、char引数はUTF-16コード単位の0..65535（サロゲートを含む）を入口で検査し、範囲外はtrapにします。引数なしの`tick()`・`GC.require()`・`GC.finish()`は、heap objectやasync call bufferを持たない現在のスカラー限定実装ではno-opです。`GC.debug`のbool状態は、初期値falseの非公開インスタンス別globalへ対応し、呼び出し間とtrap前に完了した書き込みを保持します。別インスタンスとは分離します。スカラー`print`は、hostが渡す同期callback `aloe.print_int(i64)`／`print_float(f32)`／`print_bool(i32)`／`print_byte(i32)`／`print_char(i32)`へ対応します（決定0029）。使用型だけをimportし、callback1回が1行を表します。文字列化と出力先はhostが担当し、printを使わなければimport不要です。GC phase／timingログ出力、host debug設定、他のruntime import、配列、object値は未対応です。

export関数のchar引数は、surrogateを含む0..65535のUTF-16コード単位を受け付け、範囲外のi32値をtrapで拒否します。
export関数のint引数もsigned-int32の範囲を検査し、範囲外のi64値をtrapで拒否します。
heap objectとasync CallBufferを持たない現行範囲では、引数なしの`tick()`、`GC.require()`、`GC.finish()`をno-opへloweringします。`GC.debug`のbool状態をインスタンス別に保持します。スカラーprintは型別の明示的host callbackへ対応し、GC診断ログ出力と他のhost runtime importは未対応です。

- AloeからWASMへの対応コアサブセットを拡張し、直接実行の範囲を広げる


### ツール


- CLI ツール（例: `aloec`, `aloevm`）
- フォーマッタ / シンタックスハイライト
- 簡易なパッケージ管理の仕組み（最初はシンプルなもので良い）


---


## ライセンス


Aloe は **MIT License** で公開される予定です。
詳細はリポジトリ内の `LICENSE` ファイルを参照してください。
