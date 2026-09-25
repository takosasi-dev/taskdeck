# TaskDeck — 実装規約（CLAUDE.md）

**設計書を全部読まなくても、ここだけ守れば大事故は避けられる**ものを集めた。
元は仕様書フォルダの `CLAUDE.md`。.NET 10 に上げたことと、このリポジトリの作業ルールを足してある。

- 対象: Windows 向け Todo デスクトップアプリ
- 技術: C# 14 / .NET 10 / WPF + WPF-UI / EF Core 10 + SQLite
- 性格: 個人利用、常駐、オフライン完結、単一 EXE 配布

---

## 0. 作業を始める前に

| 読むもの | 内容 |
|---|---|
| `docs/INTERFACES.md` | 担当表・契約・部品の使い方（**並列作業ではまずこれ**） |
| `docs/mockups/*.dc.html` | 画面のモックアップ（17面） |

```powershell
dotnet build TaskDeck.slnx
dotnet test TaskDeck.slnx
# 動かして確かめるときは必ず開発用フォルダで（本番のデータ・レジストリ・ホットキーを汚さない）
$env:TASKDECK_DATA_DIR = "$PWD\.devdata\mine"; dotnet run --project src/TaskDeck.App
# 性能を見るときは、新しい開発用フォルダで1万件入れて初回起動する
$env:TASKDECK_DATA_DIR = "$PWD\.devdata\10k"; $env:TASKDECK_DEV_SEED = "10000"; dotnet run --project src/TaskDeck.App
# 配布物（dist\TaskDeck.exe 1本）。main に統合したら毎回作り直す
dotnet publish src/TaskDeck.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
```

---

## 1. 絶対に守るルール

### 1.1 DateTime — 最大のバグ源

```csharp
// NG（ビルドエラーになる: BannedSymbols.txt）
var now = DateTime.Now;
var today = DateTime.Today;
var local = utc.ToLocalTime();

// OK
var now = _clock.UtcNow;            // IClock を注入する
var today = _clock.LocalToday();    // 日付の判定はローカル
var local = _clock.ToLocal(utc);
```

| ルール | 内容 |
|---|---|
| 保存は UTC | DB に入る日時はすべて UTC。`DateTimeKind.Utc` |
| 日付のみの期限 | その日のローカル 0:00 を UTC に直した値（`TaskRules.DateOnlyDue`） |
| `DateTime.Now` / `Today` / `UtcNow` を書かない | `IClock` のみ（`SystemClock` だけが例外） |
| 表示でローカルに変換 | ViewModel の境界で `IClock.ToLocal()` |
| **日付の比較はローカル** | 「今日の完了数」を UTC で数えると、深夜の完了が前日に入る |
| `DateTimeOffset` を使わない | オフセットを持ち回ると、どちらが正か分からなくなる |

### 1.2 例外を握りつぶさない

```csharp
// NG
try { ... } catch { }
try { ... } catch (Exception) { }

// OK
try { ... }
catch (SqliteException ex) { _logger.LogError(ex, "..."); throw; }
```

`CA1031` は warning。最上位ハンドラ（`App.xaml.cs`）だけが例外で、そこでも必ずログに残す。
ログの書き込み失敗だけは握りつぶしてよい（アプリを止める理由にならない）。

### 1.3 WPF

| ルール | 理由 |
|---|---|
| 色は必ず `DynamicResource` | `StaticResource` だとテーマ切替が反映されない |
| 寸法・時間は `StaticResource` | テーマで変わらないため |
| **`ScrollViewer` で `ItemsControl` を包まない** | 仮想化が無効になり、1万件でフリーズする |
| `DropShadowEffect` を使わない | ソフトウェア描画になり表示が遅れる。枠外マージン＋Border 重ねで描く（`PopupShadowStyle`） |
| アイコンは `StreamGeometry`（`Resources/Icons.xaml`） | フォント依存だと未導入環境で豆腐になる |
| `Typography.NumeralAlignment="Tabular"` | 期限・件数など数字が並ぶ欄で桁を揃える |
| WPF のプロジェクトでは `System.IO` を明示して `using` する | 暗黙の using に入っていない（`Path` が図形の `Path` とぶつかるため） |

### 1.4 データアクセス

| ルール | 理由 |
|---|---|
| `IDbContextFactory<T>` を使う | DbContext を使い回すとスレッド安全性で落ちる |
| **Core は EF Core に依存しない** | エンティティに `[Key]` `[MaxLength]` を付けない |
| 設定は Data 側に書く | `IEntityTypeConfiguration<T>` の Fluent API で |
| SQL で絞ってから読む | 全件をメモリに載せて LINQ でフィルタしない |
| トランザクション境界 | リポジトリのメソッド1回＝1トランザクション。複数表にまたがる操作も1メソッドにまとめる（UnitOfWork は作らない） |
| 変更の通知 | リポジトリがコミット後に `DataChangeHub` へ出す。UI は購読して読み直す |

### 1.5 色を変えたら検証する

```bash
node tools/validate-palette.js "#0067C0,#CA5010,#8764B8,#0E7C5A,#C2417A,#8A6D00" --mode light
```

| 基準 | 値 |
|---|---|
| 隣接ペアのΔE（色覚） | 8以上（6〜8は凡例＋直接ラベル併用が条件） |
| 隣接ペアのΔE（正常色覚） | 15以上 |
| 彩度 | 0.1以上（下回ると灰色に見える） |
| 地とのコントラスト | 3:1以上 |

**ダークテーマのプロジェクト色は検証を通った値だけを使う。** 値を先に決め打ちしない。

### 1.6 外部API

| ルール | 内容 |
|---|---|
| すべて任意機能 | 全部落ちてもアプリは完全に動く |
| **タスクの内容を送らない** | 送ってよいのは国コード、丸めた緯度経度、URL、ハッシュ接頭辞、リポジトリ名だけ |
| 失敗を通知しない | ダイアログを出さない。ログだけ残す |
| `IHttpClientFactory` を使う | `new HttpClient()` を書かない（ビルドエラーになる） |
| タイムアウト5秒・リトライ1回（2秒後）・429 で1時間止める | UI スレッドをブロックしない |

### 1.7 日本語入力（IME）

```xml
<TextBox input:ImeGuard.IsEnabled="True" PreviewKeyDown="OnKeyDown" />
```
```csharp
if (ImeGuard.IsImeEnter(e, textBox)) return;   // 変換確定の Enter で登録しない
```

| 場面 | 対応 |
|---|---|
| タスク登録の Enter | 変換確定 Enter で暴発させない（`ImeGuard.IsImeEnter`） |
| インクリメンタル検索 | 変換中は走らせない。`ImeGuard.CompositionCompleted` で走らせる |
| クイック入力のパース | 同上 |
| 全角スペース・全角記号 | パーサで半角に正規化してから処理（`TextNormalizer`） |

---

## 2. ディレクトリ構成

```
TaskDeck.slnx
├── src/
│   ├── TaskDeck.Core/      エンティティ、業務ロジック、インタフェース（EF にも WPF にも依存しない）
│   ├── TaskDeck.Data/      EF Core、リポジトリ実装、バックアップ、外部APIクライアント
│   └── TaskDeck.App/       WPF（View / ViewModel / DI / OS連携）
├── tests/TaskDeck.Tests/
├── tools/                  validate-palette.js など
└── docs/                   INTERFACES.md・モックアップ
```

依存の向きは **App → Core ← Data** の一方向（App は起動時の登録のためだけに Data も参照する）。

---

## 3. 命名

| 対象 | 規則 |
|---|---|
| ViewModel | `MainViewModel`、`TaskListViewModel` |
| リポジトリ | インタフェース `ITaskRepository`（Core）／実装 `TaskRepository`（Data） |
| サービス | `RecurrenceEngine`、`TemplateExpander`、`StatsCalculator` |
| バックグラウンド | `ReminderWorker`（`IHostedService`） |
| テスト | `対象クラス名Tests`。メソッドは `対象_条件_期待結果` |

---

## 4. テスト

| 対象 | 方針 |
|---|---|
| `RecurrenceEngine` | **必須**。設計書 4.1 の境界条件を全件 |
| `QuickInputParser` | **必須**。入力と期待結果の表駆動 |
| `TemplateExpander` | **必須**。設計書 4.5 の境界条件を全件 |
| リポジトリ | SQLite in-memory（`TestDatabase`） |
| ViewModel | NSubstitute でリポジトリをモック |
| View (XAML) | 自動テストしない。起動してスクリーンショットで確認 |

**外部APIを実際に叩かない。** `HttpMessageHandler` をモックして、タイムアウト・429・500・不正JSONを再現する。
**`IClock` を注入して時刻を固定する（`FixedClock`、JST）。** 「2月29日の翌年」「月末」のテストが書けなくなる。

---

## 5. やらないこと

機能を足したくなったら、まずここを見る。

タイマー・作業時間計測／タグの階層／タスクの依存関係／添付ファイル／共同編集／モバイル／Web版／AIによる自動分類／ガントチャート／メモ機能／多言語対応／macOS・Linux／Obsidianとの双方向同期／ユーザー定義テーマ／プラグイン機構

今回の範囲外: Phase 5（同期・ログイン・競合ダイアログ）、他アプリの CSV 取り込み、検索の高度構文（`due:today`）、印刷、自動更新（Velopack）、コード署名。

---

## 6. 実装の順序

| Phase | 内容 | 到達点 |
|:-:|---|---|
| 0 | DI、EF Core、ログ、多重起動防止、バックアップ | 空ウィンドウが出る |
| 1 | タスクCRUD、一覧、IME対応、Undo基盤 | **ここから実際に使い始める** |
| 2 | プロジェクト・タグ・優先度・各ビュー・検索・テーマ | 実用ライン |
| 3 | サブタスク・繰り返し・テンプレート・一括操作 | |
| 4 | 常駐・ホットキー・クイック入力・通知・カレンダー・パレット・振り返り | **中心価値** |
| 5 | 同期（Supabase） | 任意機能（今回は作らない） |
| 6 | 単一EXE・アイコン・起動画面・初回起動・README | |

**各 Phase 終了時点で必ず動作する状態を保つ。**

---

## 7. 迷ったときの判断基準

UI設計書 19章のUX原則。

1. **入力の手数を最小にする** — パースに失敗しても入力は必ず登録される、を何より優先
2. **確認より取り消し** — 確認ダイアログは戻せない操作だけ
3. **結果を先に見せる** — 設定の意味を説明するより、結果を1行出す
4. **見えるものを絞る** — 情報を足すより、今日に関係ないものを外す
5. **OSの作法に従う** — 独自性は見た目ではなく、入力の速さと取り消しやすさに置く
