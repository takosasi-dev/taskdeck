# TaskDeck 契約書（INTERFACES）

並列で作業する担当どうしの約束。**コードが正本**で、ここは「どこに何があり、どう使うか」と「シグネチャからは読めない意味」を書く。

---

## 0. 全員のルール

1. **担当表の自分の行にあるファイルだけを作る・変える。** 他は読むだけ。必要な変更が担当の外にあれば、手を出さず報告に書く
2. **公開シグネチャを変えない。** 仮実装（「仮実装」「波N で実装」と書いたクラス）は中身を作り直してよいが、public な型・メンバーの名前と引数は残す（足すのはよい）。XAML のリソースキー（色・寸法・書体・スタイル・アイコン）も同じ扱いで、消したり名前を変えたりしない
3. **スキーマを変えない。** `src/TaskDeck.Data/TaskDeckDbContext.cs`・`src/TaskDeck.Data/Configurations/*`・`src/TaskDeck.Data/Migrations/*` は全員触らない。列やインデックスが要るなら報告する
4. **パッケージを足さない。** `Directory.Packages.props` と各 `*.csproj` は全員触らない
5. `CLAUDE.md` の規約（DateTime・例外・WPF・IME・外部API）を守る。`DateTime.Now` などはビルドエラーになる（`BannedSymbols.txt`）
6. 終わったら `dotnet build TaskDeck.slnx` と `dotnet test TaskDeck.slnx` が通る状態でコミットする。警告を増やさない
7. ログにタスクのタイトル・メモを出さない（ID だけ）

共有ファイル（全員触らない）: `src/TaskDeck.App/App.xaml`、`src/TaskDeck.App/App.xaml.cs`、`src/TaskDeck.App/Registration/ServiceRegistration.cs`、`src/TaskDeck.App/Services/ShellService.cs`、`src/TaskDeck.App/Services/UndoService.cs`、`src/TaskDeck.App/Services/SettingsStore.cs`、`src/TaskDeck.App/Input/ImeGuard.cs`、`src/TaskDeck.App/AppServices.cs`、`src/TaskDeck.App/AppPaths.cs`、`src/TaskDeck.App/Controls/Pickers/PickerContracts.cs`、`src/TaskDeck.Core/Abstractions/*`、`src/TaskDeck.Core/Entities/*`、`src/TaskDeck.Core/Queries/*`、`src/TaskDeck.Core/Settings/*`、`src/TaskDeck.Core/Time/*`、`src/TaskDeck.Core/Enums.cs`、`src/TaskDeck.Core/Results/*`、`src/TaskDeck.Core/Services/{UndoStack,DataChangeHub,TaskRules,TextNormalizer,BusinessDays}.cs`、`src/TaskDeck.Data/Backup/*`、`src/TaskDeck.Data/DatabaseInitializer.cs`、`src/TaskDeck.Data/Infrastructure/*`、`tests/TaskDeck.Tests/TestSupport/*`、`tests/TaskDeck.Tests/Foundation/*`、`docs/INTERFACES.md`、`CLAUDE.md`。

---

## 1. 担当表

### 波1（Phase 1〜2）

| 担当 | ファイル |
|---|---|
| A（データ層） | `src/TaskDeck.Data/Repositories/*`、`src/TaskDeck.Data/Maintenance/*`、`src/TaskDeck.Data/Seeding/*`、`src/TaskDeck.Data/DataServiceCollectionExtensions.cs`、`tests/TaskDeck.Tests/Data/*` |
| B（純粋ロジック） | `src/TaskDeck.Core/Services/QuickInputParser.cs`、`src/TaskDeck.Core/Services/QuickInput/*`、`src/TaskDeck.Core/Services/RecurrenceEngine.cs`、`src/TaskDeck.Core/Services/Recurrence/*`、`src/TaskDeck.Core/Services/TemplateExpander.cs`、`src/TaskDeck.Core/Services/StatsCalculator.cs`、`tests/TaskDeck.Tests/Core/*` |
| C（メイン画面） | `src/TaskDeck.App/Views/MainWindow.xaml`、`src/TaskDeck.App/Views/MainWindow.xaml.cs`、`src/TaskDeck.App/Views/Main/*`、`src/TaskDeck.App/ViewModels/*`、`src/TaskDeck.App/Converters/*`、`src/TaskDeck.App/Behaviors/*`、`src/TaskDeck.App/Resources/Styles/TaskRow.xaml`、`src/TaskDeck.App/Resources/Styles/Chips.xaml`、`src/TaskDeck.App/Resources/Styles/NavigationItem.xaml`、`src/TaskDeck.App/Registration/ServiceRegistration.Shell.cs`、`tests/TaskDeck.Tests/App/ViewModels/*` |
| D（テーマ・入力補助・設定） | `src/TaskDeck.App/Resources/Themes/*`、`src/TaskDeck.App/Resources/Styles/Buttons.xaml`、`src/TaskDeck.App/Resources/Styles/CheckBoxes.xaml`、`src/TaskDeck.App/Resources/Styles/Inputs.xaml`、`src/TaskDeck.App/Resources/Styles/Popups.xaml`、`src/TaskDeck.App/Resources/Common/*`、`src/TaskDeck.App/Services/ThemeService.cs`、`src/TaskDeck.App/Controls/Pickers/DatePickerPanel.*`、`src/TaskDeck.App/Controls/Pickers/PriorityPickerPanel.*`、`src/TaskDeck.App/Controls/Pickers/ProjectPickerPanel.*`、`src/TaskDeck.App/Controls/Pickers/PickerConverters.cs`、`src/TaskDeck.App/Controls/Pickers/QuickDates.cs`、`src/TaskDeck.App/Settings/*`、`src/TaskDeck.App/Registration/ServiceRegistration.Appearance.cs`、`src/TaskDeck.Core/Services/ProjectPalette.cs`、`tools/*`、`tests/TaskDeck.Tests/App/Appearance/*` |

`src/TaskDeck.App/Resources/Icons.xaml` は C と D のどちらも触らない（足したいアイコンは自分の XAML の Resources に置く）。

### 波2（Phase 3）の先行分 — 波1の C・D と同時に進める

| 担当 | ファイル |
|---|---|
| F（繰り返し設定・テンプレート） | `src/TaskDeck.App/Controls/Pickers/RecurrencePickerPanel.*`、`src/TaskDeck.App/Views/Templates/*`、`src/TaskDeck.App/Registration/ServiceRegistration.Templates.cs`、`tests/TaskDeck.Tests/Templates/*` |
| G（入出力・絞り込み） | `src/TaskDeck.Data/ImportExport/*`、`src/TaskDeck.App/Controls/FilterPanel.*`、`src/TaskDeck.App/Views/DataTools/*`、`src/TaskDeck.App/Registration/ServiceRegistration.DataTools.cs`、`tests/TaskDeck.Tests/ImportExport/*` |

- F と G の ViewModel は自分の画面のフォルダ（`Views/Templates/`・`Views/DataTools/`）に置く（`ViewModels/` は C の担当）
- 波2-E（サブタスク・ドラッグ・複数選択・一括操作）は C の画面を直接変えるので、波1-C の統合後に始める。担当表はそのときに足す

### 波2-E と波3（I・J・K）— 波1・波2先行分の統合後に同時に進める

| 担当 | ファイル |
|---|---|
| E（構造化の操作・メイン画面の続き） | `src/TaskDeck.App/Views/MainWindow.xaml`、`src/TaskDeck.App/Views/MainWindow.xaml.cs`、`src/TaskDeck.App/Views/Main/*`、`src/TaskDeck.App/ViewModels/*`、`src/TaskDeck.App/Converters/*`、`src/TaskDeck.App/Behaviors/*`、`src/TaskDeck.App/Resources/Styles/TaskRow.xaml`、`src/TaskDeck.App/Resources/Styles/Chips.xaml`、`src/TaskDeck.App/Resources/Styles/NavigationItem.xaml`、`src/TaskDeck.App/Controls/FilterPanel.*`、`src/TaskDeck.App/Registration/ServiceRegistration.Shell.cs`、`tests/TaskDeck.Tests/App/ViewModels/*` |
| I（カレンダー） | `src/TaskDeck.App/Views/Calendar/*`、`src/TaskDeck.App/Registration/ServiceRegistration.Calendar.cs`、`tests/TaskDeck.Tests/Calendar/*` |
| J（パレット・ショートカット一覧・フォーカスモード） | `src/TaskDeck.App/Views/Palette/*`、`src/TaskDeck.App/Views/Shortcuts/*`、`src/TaskDeck.App/Views/Focus/*`、`src/TaskDeck.App/Registration/ServiceRegistration.Palette.cs`、`tests/TaskDeck.Tests/Palette/*` |
| K（振り返り・Markdown・外部API） | `src/TaskDeck.App/Views/Stats/*`、`src/TaskDeck.App/Controls/LinkPreviewList.*`、`src/TaskDeck.App/BackgroundTasks/*`、`src/TaskDeck.App/Settings/Pages/ExternalPage*`、`src/TaskDeck.App/Settings/Pages/AboutPage*`、`src/TaskDeck.Data/External/*`、`src/TaskDeck.Data/Export/*`、`src/TaskDeck.App/Registration/ServiceRegistration.Insights.cs`、`tests/TaskDeck.Tests/Insights/*` |

- ViewModel は自分の画面のフォルダに置く（`ViewModels/` は E の担当）

### 波3の残り（H・J・N）— 波2-E・I・K の統合後に同時に進める

| 担当 | ファイル |
|---|---|
| H（常駐・ホットキー・クイック入力・通知・自動起動） | `src/TaskDeck.App/Residency/*`（新規。トレイ・ホットキー・通知・自動起動の本体）、`src/TaskDeck.App/Views/QuickInput/*`、`src/TaskDeck.App/BackgroundTasks/ReminderWorker.cs`、`src/TaskDeck.App/Settings/Pages/{GeneralPage,NotificationsPage,ShortcutsPage}*`、`src/TaskDeck.App/Registration/ServiceRegistration.Residency.cs`、`src/TaskDeck.Core/Reminders/*`（新規。通知のまとめ方などの純粋ロジック）、`tests/TaskDeck.Tests/Residency/*`（新規）、`tests/TaskDeck.Tests/App/Appearance/{GeneralPage,NotificationsPage,ShortcutsPage}ViewModelTests.cs`（既存テストの手直しだけ） |
| J（パレット・ショートカット一覧・フォーカスモード） | 上の表の J の行のまま |
| N（使い捨てリスト） | `src/TaskDeck.Core/Scratch/*`（新規。モデル・保存・字下げの規則・テンプレートとの変換）、`src/TaskDeck.App/Views/Scratch/*`（新規。中身の部品・小窓・ViewModel・開き方のサービス）、`src/TaskDeck.App/Registration/ServiceRegistration.Scratch.cs`（新規）、メイン画面への組み込み（`src/TaskDeck.App/Views/MainWindow.xaml`・`MainWindow.xaml.cs`・`Views/Main/*`・`ViewModels/*`）、テンプレート画面への入口（`src/TaskDeck.App/Views/Templates/*`）、`tests/TaskDeck.Tests/Scratch/*`（新規）、`tests/TaskDeck.Tests/App/ViewModels/*`・`tests/TaskDeck.Tests/Templates/*`（既存テストの手直しだけ） |

### 波4（Phase 6）— 波3の統合後に同時に進める

| 担当 | ファイル |
|---|---|
| L（起動画面・初回起動） | `src/TaskDeck.App/Views/Startup/*`（仮実装あり。起動画面 `SplashWindow`・初回起動 `FirstRunWindow` とその ViewModel・`FirstRunService`）、`src/TaskDeck.App/Registration/ServiceRegistration.Startup.cs`、`tests/TaskDeck.Tests/Startup/*`（新規） |
| M（アイコン） | `src/TaskDeck.App/Resources/Brand/*`（アイコンの形。キーは変えない）、`src/TaskDeck.App/Assets/*`（新規。`.ico` と PNG）、`tools/IconGen/*`（新規。書き出しの道具。ソリューションには入れない）、`src/TaskDeck.App/Residency/TrayIconService.cs` のうち `TrayIconRenderer`（トレイの絵だけ）、`tests/TaskDeck.Tests/Brand/*`（新規） |
| 統合担当 | 起動の流れ（`App.xaml.cs`・`ShellService`）、`*.csproj`（`ApplicationIcon` など）、起動時間、README、配布 |

- 窓のアイコン（タイトルバー・タスクバー）と exe のアイコンは、M の `.ico` を統合担当が `ApplicationIcon` と `ShellService` でつなぐ（M は `*.csproj` に触らない）

---

## 2. 時刻と日付（`src/TaskDeck.Core/Time/Clock.cs`）

- 現在時刻は `IClock.UtcNow` だけ。ローカル変換は拡張メソッド: `clock.LocalNow()` `clock.LocalToday()` `clock.ToLocal(utc)` `clock.ToUtc(local)` `clock.ToLocalDate(utc)` `clock.LocalDayStartUtc(date)` `clock.LocalToUtc(date, time)`
- **日付のみの期限** = `DueHasTime=false` かつ `DueAt = clock.LocalDayStartUtc(その日)`。作るときは `TaskRules.DateOnlyDue(date, clock)`。表示は `clock.ToLocalDate(DueAt)`
- **期限切れ**: `TaskRules.IsOverdue(task, clock)`（日付のみ＝今日より前、時刻あり＝現在より前。未完了のみ）
- 区分（一覧のグループ・色）: `TaskRules.Categorize(task, clock)` → `Overdue / Today / Tomorrow / ThisWeek / Later / None`
- 通知日時: `TaskRules.ComputeRemindAt(dueAt, hasTime, offsetMinutes, defaultReminderTime, clock)`（リポジトリが自動で再計算する。UI は RemindOffsetMinutes を入れるだけ）

## 3. データの読み書き

### 3.1 エンティティ（`src/TaskDeck.Core/Entities/*`）

`TaskItem` `Project` `Tag` `TaskTag` `RecurrenceRule` `TaskTemplate` `TaskTemplateItem` `SyncMeta` `AppStateEntry` `Holiday` `WeatherDay` `LinkPreview`。
全部 POCO。日時は UTC。`TaskItem.Clone()` は全列の複製。`TaskItem.IsOpen`（未着手・進行中）。
中止（Cancelled）にも `CompletedAt` が入る（閉じた日時）。振り返りの集計は `Completed` だけ。

### 3.2 一覧の条件 `TaskQuery`（`src/TaskDeck.Core/Queries/TaskQuery.cs`）

- 組み込みビューは `BuiltInViews.QueryFor(ViewKey)`: 今日＝`TodayOrOverdue`（期限切れを含む、期限順）、予定＝`Next7Days`（今日から7日、期限順）、すべて＝未完了（手動順）、完了済み＝完了と中止（閉じた日時の新しい順、サブタスクを添えない）、ゴミ箱＝削除済み、プロジェクト・タグ＝未完了のうち該当
- 検索は `query with { SearchText = "..." }`。空白区切りの AND、NFKC・小文字で比べる（全角「ＡＷＳ」で「AWS」が当たる）
- `IncludeSubtasks=true` なら、条件に合ったタスクの子孫も結果に入る（`TaskListRow.IsContext=true`）。**並べ替えは `TaskTree.Arrange(rows)`**（親の直後に子孫、字下げ `Level`）
- `ViewKey`（`Today` `Project:{guid}` など）は `ToString()` / `TryParse` で設定に保存する

### 3.3 リポジトリ（`src/TaskDeck.Core/Abstractions/*`、実装は `src/TaskDeck.Data/Repositories/*`）

- すべて非同期・スレッドセーフ（呼ぶたびに DbContext を作って捨てる）。UI スレッドから `await` してよい
- 書き込みはメソッド1回＝1トランザクション。成功すると `DataChangeHub.Changed` が発火する（**UI スレッド以外で発火することがある**。購読側は Dispatcher に戻す）
- `ITaskRepository` の書き込みは `TaskMutationResult` を返す: `Changes`（取り消し用）・`Affected`（書き込み後）・`Created`（新規。繰り返しの次回もここ）
- `UpdateAsync(id, t => { ... })` で変えてよい列は Title / Notes / Status / Priority / DueAt / DueHasTime / RemindAt / RemindOffsetMinutes / ProjectId / DurationMinutes だけ。親子は `SetParentAsync`、並びは `ReorderAsync`、繰り返しは `SetRecurrenceAsync`、削除は `SoftDeleteAsync`
- 追加 `AddAsync(new NewTaskRequest { Title = ..., ProjectName = "業務改善", TagNames = ["仕事"] })` は、無いプロジェクト・タグを同じトランザクションで作る
- 不変条件（タイトルの正規化・空タイトルは元に戻す・完了日時・通知日時・繰り返しの次回生成・親削除で子を昇格）はリポジトリが守る。詳しくは `ITaskRepository` の doc コメント
- 並び順の値は `SortOrderMath.Between(前の SortOrder, 後ろの SortOrder)`
- 全メソッド実装済み（波1-A）
- スレッド: 件数の多い読み込み（`QueryAsync` `GetViewCountsAsync` `GetCompletedFactsAsync` `GetInDueRangeAsync`）は中でスレッドプールに移して走る（Microsoft.Data.Sqlite の非同期は中身が同期のため）。`await` の後は呼び出し元のスレッドに戻るので、UI からそのまま `await` してよい。書き込みと1件の読み込みは呼び出し元のスレッドで走る（`UpdateAsync` の mutate も）
- 振り返りは `GetCompletedFactsAsync(null)`（全期間）を `StatsCalculator.Calculate` に渡す（自己最長の連続日数と26週のヒートマップに全期間が要る）

### 3.4 取り消し（`src/TaskDeck.App/Services/UndoService.cs`、`src/TaskDeck.Core/Services/UndoStack.cs`）

```csharp
var result = await tasks.SoftDeleteAsync([id]);
undo.Record(result, $"「{title}」を削除しました", UndoKind.Delete, showToast: true);
// Ctrl+Z / トーストの「元に戻す」
await undo.UndoLatestAsync();
```

- 10段。`UndoKind.TextEdit` と `UndoKind.Bulk` は最新の1件だけ残る
- トーストを出す操作（UI 設計書 13.1）: 削除・一括操作・繰り返しタスクの完了（「完了しました・次回は 9月29日（月）」）・テンプレート展開・繰り越し。繰り返しでない完了は出さない（Ctrl+Z では戻せる）
- `UndoService.ToastRequested` をメイン画面のトースト欄が購読する

### 3.5 変更の通知（`src/TaskDeck.Core/Services/DataChangeHub.cs`）

`DataChangeKind`（Tasks / Projects / Tags / Templates / ExternalCache）と、変わったタスクの Id。一覧・サイドバーの件数・トレイは、これを受けて読み直す。

## 4. 設定（`src/TaskDeck.Core/Settings/AppSettings.cs`）

- `ISettingsStore.Current` で読む。変えるときは `settings.Update(s => s.Appearance.Theme = AppThemeMode.Dark)`（即保存・`Changed` 発火）
- 項目は全部 `AppSettings` にある（全般・外観・通知・ホットキー・カレンダー・データ・外部・ウィンドウ配置・保存済みフィルタ・初回起動済み）。項目を足したいときは報告する
- 同期（Phase 5）の設定は無い

## 5. アプリの部品

### 5.1 画面の出し入れ（`src/TaskDeck.App/Services/ShellService.cs`）

| メソッド | 開くもの | 中身の担当 |
|---|---|---|
| `ShowMainWindow()` / `HideMainWindow()` / `ExitApplication()` | メイン画面（Esc の最後の段は `HideMainWindow`） | C |
| `OpenSettings(page)` | `Settings.SettingsWindow`（page: general / appearance / notifications / shortcuts / external / data / about。一覧は `SettingsWindow.PageNames`） | D |
| `OpenTemplates()` | `Views.Templates.TemplatesWindow` | 波2-F |
| `OpenShortcuts()` | `Views.Shortcuts.ShortcutsWindow` | 波3-J |
| `OpenCommandPalette()` | `Views.Palette.CommandPaletteWindow` | 波3-J |
| `OpenFocusMode()` | `Views.Focus.FocusWindow` | 波3-J |
| `OpenQuickInput()` | `Views.QuickInput.QuickInputWindow` | 波3-H |
| `ShowToolWindow(window)` | ダイアログではない常駐の小窓（使い捨てリストの小窓など）。窓は呼び出し側が作って渡す。確認用の起動（`TASKDECK_DEV_NOACTIVATE=1`）では前面もフォーカスも奪わない | 波3-N |
| `PrepareMainWindow()` | 起動時、DB の準備（スレッドプール）を待つ間に UI スレッドでメイン画面を作っておく（出さない）。窓を出さない起動では呼ばない | 統合担当（起動の速さ） |
| `StartUp(args, firstRun)` | `--tray`（自動起動）と `-ToastActivated`（止まっている間に通知を押された）は、トレイがあれば何も出さない（`IsBackgroundStart(args)`）。`firstRun` なら `Views.Startup.FirstRunWindow` を出し、閉じたらメイン画面を出す。出した窓を返す（何も出さなければ null。App はその窓が描けたら起動画面を消す） | 統合担当（トレイは波3-H、初回起動は波4-L） |
| `ShowMainWindow()` | 初めて出すときは `MainWindow.BeginInitialLoad()`（起動時の読み込み。1回だけ）を先に始めてから出す（クエリはスレッドプールで進み、窓を出す処理と並ぶ）。メイン画面の読み込みは `OnLoaded` でもこれを待つ | 統合担当 |
| `NotifyUser(message)` / `UserNotice` | 画面下の短い知らせ（未処理例外など）。メイン画面のトースト欄が購読する。購読される前（トレイだけで動いている間）の分は新しい3件まで溜め、購読されたときに1行で渡す | C |
| `NavigateTo(view)` / `NavigateRequested` | メイン画面を前に出して、指定のビューに切り替える（パレット・トレイ・通知から） | E が購読して切り替える |
| `RevealTask(id)` / `RevealTaskRequested` | メイン画面を前に出し、そのタスクが出るビューで選んで詳細ペインを開く（パレットの検索結果・通知のクリックから） | E が購読して見せる |

ダイアログ（設定・テンプレート・ショートカット一覧）は同時に1つだけ。画面は DI から取り出すので、各担当は自分の `Registration/ServiceRegistration.*.cs` に登録する。

メイン画面の状態を他の画面が読むためのプロパティ（書くのはメイン画面の担当だけ）:

| プロパティ | 中身 | 書く | 読む |
|---|---|---|---|
| `SelectedTaskIds`（`IReadOnlyList<Guid>`） | 一覧で選んでいるタスク。選択が変わるたびに入れる（複数選択のときは全部。波2-E で対応済み） | C | テンプレート画面の「選択中のタスクから作る」（F） |
| `CurrentListQuery`（`TaskQuery?`） | 一覧がいま出している条件（検索・絞り込み・並び順を含む）。カレンダー・振り返りを出している間は null | C | CSV エクスポート「表示中の一覧」（G） |

### 5.2 メイン画面に置く差し込み口（C が置き、中身は各担当）

| 部品 | 置き場所 | 公開 API | 中身の担当 |
|---|---|---|---|
| `Views.Calendar.CalendarView` | 中央（ビューがカレンダーのとき一覧の代わり） | `SelectedTaskId`（Guid?、双方向） | 波3-I |
| `Views.Stats.StatsView` | 中央（ビューが振り返りのとき） | なし | 波3-K |
| `Views.Scratch.ScratchPaneView`（DataContext は `MainViewModel.Scratch`） | 中央（ビューが `ViewKind.Scratch` のとき）。小窓に切り離している間は「小窓で表示中［戻す］」 | なし | 波3-N |
| `Controls.FilterPanel` | 一覧の見出しの「絞り込み」から Popup | `Query`、`BaseQuery`、`Applied(FilterAppliedEventArgs)`、`Cancelled` | 波2-G（`BaseQuery` は波2-E） |
| `Controls.LinkPreviewList` | 詳細ペインのメモの下 | `Text`（メモ本文） | 波3-K |

### 5.3 入力補助ポップアップ（`src/TaskDeck.App/Controls/Pickers/*`）

ホスト（詳細ペインなど）は Popup（`StaysOpen=False`）にパネルを入れ、開く前に現在値を DP に入れ、`Picked` で保存して閉じ、`Cancelled`（Esc）で閉じる。パネルは DB に書かない（プロジェクトの「新しく作る」だけ例外）。

**枠はパネルが持つ**（ピッカー各種と `FilterPanel`）。パネルの一番外側が `PopupShadowStyle` ＋ `PopupSurfaceStyle` の Border 2枚で、幅も内側の面に付いている。ホストは枠を足さずに次の形で置く（包むと枠が二重になる）。影の余白が左右10・上8・下14あるので、位置はその分ずらす。パネルは開くと（Loaded で）自分でフォーカスを取るので、ホストは横取りしない。

```xml
<Popup AllowsTransparency="True" StaysOpen="False" Placement="Bottom" HorizontalOffset="-10" VerticalOffset="-8">
  <pickers:DatePickerPanel Picked="OnDuePicked" Cancelled="OnPopupCancelled" />
</Popup>
```

| パネル | 入れる値 | Picked の中身 | 中身の担当 |
|---|---|---|---|
| `DatePickerPanel` | `Date`（DateOnly?）, `Time`（TimeOnly?） | `DatePickedEventArgs(Date, Time)`（Date=null で期限を消す、Time=null で終日） | D |
| `PriorityPickerPanel` | `Priority` | `PriorityPickedEventArgs(Priority)` | D |
| `ProjectPickerPanel` | `ProjectId`（Guid?） | `ProjectPickedEventArgs(ProjectId)`（null で「なし」） | D |
| `RecurrencePickerPanel` | `Recurrence`（RecurrenceInput?）, `BaseDueAt`（UTC）, `DueHasTime` | `RecurrencePickedEventArgs(Recurrence)`（null で解除） | 波2-F |

### 5.4 IME（`src/TaskDeck.App/Input/ImeGuard.cs`）

```xml
<TextBox input:ImeGuard.IsEnabled="True" PreviewKeyDown="OnKeyDown" />
```
```csharp
if (ImeGuard.IsImeEnter(e, textBox)) return;   // 変換確定の Enter では何もしない
```
インクリメンタル検索・パースは `ImeGuard.IsComposing(textBox)` の間は走らせず、`ImeGuard.CompositionCompleted`（ルーティングイベント）で走らせる。タスクを入力する欄・検索欄には必ず付ける。

### 5.5 XAML から作る部品がサービスを使うとき（`src/TaskDeck.App/AppServices.cs`）

`AppServices.Get<IClock>()` など。ViewModel や普通のクラスはコンストラクタで受け取る（こちらは使わない）。

### 5.6 DI の登録（`src/TaskDeck.App/Registration/*`）

`ServiceRegistration.cs`（共有）が共通サービスを登録し、担当ごとの partial メソッド `AddShell` / `AddAppearance` / `AddTemplates` / `AddDataTools` / `AddResidency` / `AddCalendar` / `AddPalette` / `AddInsights` / `AddScratch` / `AddStartup` を呼ぶ。自分のファイルの中だけに登録を書く。

登録済みの共通サービス: `AppPaths`、`SettingsStore` / `ISettingsStore`、`IClock`、`DataChangeHub`、`UndoStack`、`UndoService`、`ThemeService`、`ShellService`、`QuickInputParser`、`IRecurrenceEngine`、`TemplateExpander`、`StatsCalculator`、Data 層一式（リポジトリ5つ・`BackupService`・`DatabaseInitializer`・`HolidayCache` / `IHolidayProvider`・`WeatherCache` / `IWeatherProvider`・`IDbContextFactory<TaskDeckDbContext>`・`DatabaseMaintenance`・`SampleDataSeeder`・`DevDataSeeder`）。

起動時の初回処理（`App.xaml.cs`）: 初期テンプレート3件（`ITemplateRepository.SeedDefaultsAsync`）→ 初回サンプル（`SampleDataSeeder`）→ 画面表示の10秒後に裏で保守（`DatabaseMaintenance`）。開発用データフォルダでは `TASKDECK_DEV_SEED=10000` を付けて初回起動すると、サンプルの代わりに1万件が入る。

### 5.7 波2の先行分（F・G）の約束

**F: 繰り返しピッカーとテンプレート画面**
- `RecurrencePickerPanel` の公開 API は 5.3 のとおり（変えない）。規則の組み立て・説明文・次回の計算は `RecurrencePresets` `RecurrenceText` `IRecurrenceEngine` を使う（自前で RRULE を解釈しない）
- テンプレート画面は `ShellService.OpenTemplates()` で開く `Views.Templates.TemplatesWindow`（DI に Transient で登録済み）
- 展開: `TemplateExpander.Plan(template, items, options)` で計画 → プレビュー → `ITemplateRepository.ExpandAsync(plan)` → `undo.Record(result, "「週次レビュー」から 5 件を作りました", UndoKind.Bulk, showToast: true)`
- 「選択中のタスクから作る」: `ShellService.SelectedTaskIds` を `ITemplateRepository.CreateFromTasksAsync(name, ids, 基準日)` に渡す。空なら「メイン画面でタスクを選んでから押してください」と出す

**G: 入出力と複合絞り込み**
- 入出力の本体は `src/TaskDeck.Data/ImportExport/*`（`IDbContextFactory` で読み書き。App の型に依存しない）。画面は `Views.DataTools.ImportExportPanel`（UserControl）として作り、設定画面の「データ」ページへの組み込みは統合担当がやる
- JSON の置換インポートは、確認ダイアログの後、書き込む前に `BackupService.CreateStartupBackup()` で1世代とり、1トランザクションで入れ替える。インポートの後は `DataChangeHub.Publish(Tasks | Projects | Tags | Templates)` を出す
- CSV「表示中の一覧」: `ShellService.CurrentListQuery` を `ITaskRepository.QueryAsync` に渡して、並びは `TaskTree.Arrange` のとおりに書く。null（一覧を出していない）なら「すべて」ビューの条件で書く
- `FilterPanel` は受け取った `Query` の絞り込みの項目（`Statuses` `Due` `DueFrom` `DueTo` `ProjectId` `WithoutProject` `TagIds` `MinPriority`）だけを変えて `Applied` で返す（`SearchText`・並び順・`IncludeSubtasks` などはそのまま）。「この条件を保存」は名前を聞いて `settings.Update(s => s.SavedFilters.Add(...))`（サイドバーに出る）

### 5.8 波2-E と波3（I・J・K）の約束

**E: メイン画面の続き**
- `ShellService.NavigateRequested` を受けたらそのビューへ切り替える。`RevealTaskRequested` を受けたら、今のビューにそのタスクが出ていればそこで、出ていなければ出るビュー（未完了は「すべて」、完了・中止は「完了済み」、削除済みは「ゴミ箱」）に切り替えて、選んで・見える位置までスクロールし・詳細ペインを開く
- `FilterPanel` に DP `BaseQuery`（`TaskQuery?`）を足す。「クリア」は絞り込みの8項目を `BaseQuery` の値に戻す（null なら `TaskQuery` の既定値）。メイン画面は開く前に、いまのビューの条件（`BuiltInViews.QueryFor` か保存済みフィルタの条件）を入れる
- 複数選択にしたら `ShellService.SelectedTaskIds` に全部入れる

**I: カレンダー（`Views.Calendar.CalendarView`、メイン画面の中央の差し込み口）**
- 公開 API は `SelectedTaskId`（Guid?、双方向）だけ。タスクを選んだらここに入れる（詳細ペインはメイン画面が出す）
- 読み込み: `ITaskRepository.GetInDueRangeAsync(from, to, includeClosed)`、繰り返しの仮表示は `GetOpenRecurringAsync` ＋ `IRecurrenceEngine.Occurrences`、祝日は `IHolidayProvider`、天気は `IWeatherProvider`（null なら出さない）。`DataChangeHub`（Tasks・ExternalCache）で読み直す
- 書き込み: 日付や所要時間の変更は `UpdateAsync`（DueAt・DueHasTime・DurationMinutes）＋ `UndoService.Record`。週の始まりは `settings.Calendar.WeekStartsOnMonday`

**J: パレット・ショートカット一覧・フォーカスモード**
- 画面を開くのは `ShellService.OpenCommandPalette()` / `OpenShortcuts()` / `OpenFocusMode()`（DI 登録は自分の `ServiceRegistration.Palette.cs`）
- パレットの実行: ビュー切替は `ShellService.NavigateTo`、タスクを開くは `RevealTask`、画面は `OpenSettings` `OpenTemplates` `OpenFocusMode` `OpenShortcuts`。作成は `QuickInputParser.Parse(text).ToRequest()` → `AddAsync`、完了は `SetCompletedAsync` ＋ 取り消しの記録、繰り越し（F-137）は `CarryOverOverdueAsync` ＋ トースト、`+テンプレート名`（F-15E）は `TemplateExpander.Plan`（基準日は今日）→ `ExpandAsync` ＋ トースト
- 使用頻度（F-138）は `AppStateKeys.PaletteUsage`、最近開いたタスクは `AppStateKeys.RecentTaskIds` に JSON で持つ（`IAppStateRepository`）
- ショートカット一覧はアプリ内のキーと、`settings.Hotkeys` のグローバルホットキーを表示だけする（変更は波3-H）

**K: 振り返り・Markdown・外部API**
- `Views.Stats.StatsView`（公開 API なし）: `StatsCalculator.Calculate` に `GetCompletedFactsAsync(null)` を渡す（3.3）
- Markdown 出力（F-108・F-109）は振り返りの画面から（期間を選んで）。出力先は `settings.Data.MarkdownExportFolder`（null なら毎回たずねる）
- 外部API: 祝日（内閣府の「国民の祝日」CSV を1日1回まで → Holiday 表を丸ごと入れ替え → `HolidayCache.ReloadAsync`）、天気（Open-Meteo → WeatherDay 表 → `WeatherCache.ReloadAsync`。設定が OFF・場所が未設定なら `Get` は null）、URL のタイトル（Microlink、既定 OFF → LinkPreview 表 → `Controls.LinkPreviewList`）、更新の確認（GitHub Releases の `takosasi-dev/taskdeck`。`/releases/latest` を見るのでプレリリースは知らせない。リポジトリ名が null の間は何もしない）。取得後は `DataChangeHub.Publish(DataChangeKind.ExternalCache)`
- 通信の決まり（仕様書 NFR 6.4）: `IHttpClientFactory` 経由、タイムアウト5秒・2秒後に1回だけ再試行・429 なら1時間止める・`settings.External.OfflineMode` で全部止める。送ってよいのは国コード・小数第2位に丸めた緯度経度・URL（Microlink を ON にしたときだけ）・リポジトリ名だけ。**タスクの内容は絶対に送らない**。テストは `HttpMessageHandler` の偽物で、本物の API を叩かない
- 設定画面の「外部サービス」と「TaskDeck について」のページは、この組では K の担当（設定を効かせる・天気の場所を選ぶ・今すぐ取得・更新の確認結果を出す）

**開発時の入口**: 自分の画面を直接開いて確かめたいときは、`AppPaths.IsDevelopment` のときだけ働く `IHostedService` を自分の Registration に置き、`TASKDECK_DEV_<担当の英字名>`（例 `TASKDECK_DEV_PALETTE`）の環境変数で開く。使用済み: `TASKDECK_DEV_OPEN`（設定・ピッカー）、`TASKDECK_DEV_TEMPLATES`、`TASKDECK_DEV_DATATOOLS`、`TASKDECK_DEV_INSIGHTS`（Microlink と GitHub を偽物に切り替える）、`TASKDECK_DEV_PALETTE`（J）、`TASKDECK_DEV_RESIDENCY`（H）、`TASKDECK_DEV_SCRATCH`（N）、`TASKDECK_DEV_FIRSTRUN`・`TASKDECK_DEV_SPLASH`（L、5.10）。H の本物の動作を依頼者が試すときだけ使うもの: `TASKDECK_DEV_HOTKEYS=1`（グローバルホットキーを登録する）、`TASKDECK_DEV_TOASTS=1`（Windows の通知を出す）

**確認作業で窓を前に出さない**: 担当が確認のために起動するときは、必ず `TASKDECK_DEV_NOACTIVATE=1` を付ける（開発用フォルダのときだけ効く）。`ShellService` が窓を出すだけで前面にもフォーカスにもしないので、依頼者が別のアプリで打っているキーを横取りしない（実際に依頼者の Ctrl+T が確認用の窓に入り、テンプレート画面が開いたことがある）。裏の窓から開いたポップアップはすぐ閉じる（6.1）ので、ポップアップの見た目はテストと、どうしても要るときだけの短い確認にとどめる

**祝日のデータ（波3-K の報告）**: Nager.Date の日本の祝日は、振替休日を元の祝日の日付ごと動かして返し（2026-05-06 が「憲法記念日」になり 5/3 が無い）、国民の休日（2026-09-22）が入っていない。依頼者の決定で、内閣府の公式 CSV（`syukujitsu.csv`、Shift_JIS、1955年〜翌年）に切り替えた（`IHolidayProvider` の使い方は変わらない。振替休日・国民の休日の名前は CSV のとおり「休日」）。`ExternalHttp` に JSON でない中身を取る `GetBytesAsync` を足した

### 5.9 波3の残り（H・N）の約束

**H: 常駐・ホットキー・クイック入力・通知・自動起動**
- 起動時の組み込み: `AddResidency` に `IHostedService` を置く。`StartAsync` は UI スレッドで、`ShellService.StartUp` より前に走る（`App.xaml.cs` の順）。ここでトレイを作り、`ShellService.IsTrayAvailable = true` にする（× と Esc の最後の段が「トレイへ隠す」に変わる。この切り替えは `ShellService` が済ませてある）
- トレイの件数は `ITaskRepository.GetViewCountsAsync`、`DataChangeHub.Changed` で読み直す（UI スレッド以外で来るので Dispatcher に戻す）。メニューからの操作は `ShellService`（`ShowMainWindow` `OpenQuickInput` `OpenSettings` `ExitApplication` など）
- ホットキーは `settings.Hotkeys`（`QuickInput` `ShowMainWindow` `FocusMode`）を `RegisterHotKey` で登録し、設定が変わったら登録し直す。押されたら `ShellService.OpenQuickInput()` / `ShowMainWindow()` / `OpenFocusMode()`。登録に失敗したキーは設定の「ショートカット」ページに警告を出す
- 通知は `ReminderWorker`（60秒ごと）: `GetDueRemindersAsync(now)` → まとめ方を決める（`Core/Reminders`）→ 出す → `MarkNotifiedAsync`。通知の「完了」は `SetCompletedAsync` ＋ `UndoService.Record`、「30分後」は `SnoozeAsync(id, now + settings.Notifications.SnoozeMinutes)`、本文のクリックは `ShellService.RevealTask(id)`。日次サマリと期限切れは `AppStateKeys.LastDailySummaryDate` / `LastOverdueNoticeDate` で1日1回。一時停止は `settings.Notifications.PausedUntilUtc`
- 止まっている間に通知が押されてアプリが起動したときは、引数に `-ToastActivated` が付く。`ShellService.StartUp` はトレイがあればメイン画面を出さないので、押された中身は H が受けて処理する
- 自動起動は HKCU の Run に `"<exe のパス>" --tray`。`settings.General.LaunchAtLogin` に合わせる
- 他の画面から使う口（波3-H で実装済み）: `HotkeyService.Pressed`（押されたキー。UI スレッド）・`StateOf(action)`（`Registered` / `InUse` / `Invalid` / `NotRegistered`）・`StatesChanged`・`IsEnabled`（開発用フォルダで `TASKDECK_DEV_HOTKEYS` が無ければ false）・`SettingOf(settings.Hotkeys, action)`（キーの文字列）。`Intercept`（統合担当が波4で追加）は押されたキーを先に受け取り、true を返すと `Pressed` を出さない（クイック入力などを開かない）。クイック入力の窓は1つを使い回す（閉じずに隠す。`QuickInputWindow.MarkRequested()` はホットキーからの表示時間を測る印）。通知の出し先は `IReminderNotifier`（本物は `ToastNotifier`。出せなかった分はアプリ内の知らせに回す）。トレイの件数は「今日」ビューの件数（期限切れがあれば期限切れの件数を赤で。99 を超えたら 99+）
- 確認の入口（`TASKDECK_DEV_RESIDENCY`、カンマ区切り）: `quick`（起動後にクイック入力）・`notify[:N]`（通知日時を過ぎたタスクを N 件足す）・`trayicons`（トレイのアイコンの絵を `<データフォルダ>\dev-tray\` に書き出す）。合図のファイル: データフォルダに `dev-exit` を作ると終了、`dev-quick` でクイック入力を開く
- **開発用フォルダ（`AppPaths.IsDevelopment`）での決まり**: HKCU の Run には絶対に書かない（ログに「書くはずだった」と出すだけ）。グローバルホットキーは `TASKDECK_DEV_HOTKEYS=1` のときだけ登録する。Windows の通知（トースト）は `TASKDECK_DEV_TOASTS=1` のときだけ出す（AUMID・COM の登録やスタートメニューのショートカット作りも、このときだけ）。それ以外は同じ中身をアプリ内の知らせ（`ShellService.NotifyUser`）とログ（ID だけ）に出す。担当の確認作業ではこの2つを付けない

**N: 使い捨てリスト**
- タスクとは別物。保存は `IAppStateRepository` の `AppStateKeys.ScratchLists`（全リストを JSON で1件）と `AppStateKeys.ScratchWindow`（最後に出した場所と小窓の位置・大きさ）。タスクの一覧・検索・件数・カレンダー・振り返り・通知・エクスポートには出ない（JSON のエクスポートは AppState を書かず、置換インポートは AppState を残す）。DB に入るのでバックアップには入る
- メイン画面の中央に出すときのビューは `ViewKey.ForScratch(listId)`（`ViewKind.Scratch`、`IsTaskList=false`）。カレンダー・振り返りと同じ扱いで、出している間は `ShellService.CurrentListQuery = null`。他の画面からメイン画面に出すときは `ShellService.NavigateTo(ViewKey.ForScratch(id))`。起動時ビューに残っていたリストが捨てられていたら「今日」を開く
- 小窓は N が作り、`ShellService.ShowToolWindow(window)` で出す（確認用の起動で前面を奪わないため。`Show()` を直接呼ばない）
- 他の画面から使うとき（波3-N で実装済み）: 一覧は `ScratchStore.Lists`（作った順。UI スレッドから）、増えた・名前が変わった・捨てたは `ScratchStore.Changed`。開くのは `ScratchPresenter.Open(id)`（最後に使った方）・`ShowInMain(id)` / `ShowInWindow(id)`、新規は `presenter.Open(presenter.CreateNew().Id)`、テンプレートからは `CreateFromTemplateAsync(templateId)`。小窓に出しているリストは `WindowListId` / `WindowListChanged`。項目はタスクではないので、タスクの検索には混ぜない。確認用の入口は `TASKDECK_DEV_SCRATCH`（`window` / `main` / `templates`）
- テンプレートとの行き来: 「使い捨てで開く」は `ITemplateRepository.GetWithItemsAsync` の項目（`ParentItemId`・`Depth`・`SortOrder` で木にして、題名だけ使う）からリストを作る。タスクは作らない。「テンプレートとして保存」は `ITemplateRepository.SaveAsync(new TaskTemplate { Name = ... }, items)`（`Title`・`ParentItemId`・`Depth`・`SortOrder` だけ入れる）→ `DataChangeHub.Publish(DataChangeKind.Templates)` はリポジトリが出す

### 5.10 波4（L・M）の約束

**L: 起動画面・初回起動**（`src/TaskDeck.App/Views/Startup/*` に仮実装と起動の流れのつなぎが入っている）
- 起動画面 `StartupSplash`（2026-09-25 に WPF の窓 `SplashWindow` から置き換え。設計メモの変えた点 #24）: WPF の窓ではなく、専用のスレッドが GDI+ で描く重ね合わせ窓（layered window）。`App.xaml.cs` が設定を読んだ直後に `StartupSplash.Show(reduceMotion, highContrast)`（すぐ返る）、`StartUp` が出した窓の `ContentRendered` で `Close()`（100ms で消える。どのスレッドから何度呼んでもよい。描かれないままでも5秒で消す）。UI スレッドが忙しくても動きが止まらないので、その間に DB の準備とメイン画面の組み立てを並べる。サービスも DI も使わない。窓を出さない起動（`--tray`・`-ToastActivated`）では出さない。ハイコントラストでは OS の色で描く
- 見た目と動きは UI 設計書 23.4 のとおり: 480×320・角丸12・地 `#14161A`・アイコン 76px（上から78px）・ロゴ 26px SemiBold 白・タグライン 12px `#7A8089` 字送り +0.04em・下端に 2px の進捗バー（読み込み中はループ）・右下にバージョン 10px `#4A4F57`（`typeof(App).Assembly.GetName().Version?.ToString(3)`、「TaskDeck について」と同じ）。動きは要素ごとに独立（連鎖させない）: 0–150ms アイコン 不透明度0→1・拡大0.94→1、100–400ms チェックを線で描く（線の始めから描き足す。始めは線の太さの 1/4 手前から）、250–400ms ロゴ 下から4px＋フェード、350–450ms タグライン フェード。`reduceMotion` なら動かさずに出し（進捗の帯も出さない）、`Close` ですぐ閉じる。アイコンは `Resources/Brand/AppIcon.xaml` の形（下の M の段）を使う
- 初回起動 `FirstRunWindow`（DI の Transient）: `ShellService.StartUp(args, firstRun: true)` が出し、閉じたらメイン画面が出る（済み）。閉じ方（最後まで・スキップ・×）によらず `FirstRunService.MarkCompletedAsync()`。出すかどうかは `FirstRunService.ShouldShowAsync()`（`AppStateKeys.FirstRunCompleted` が無ければ出す。開発用フォルダでは `TASKDECK_DEV_FIRSTRUN=1` のときだけ、毎回）
- 3画面は UI 設計書 24 のとおり（読ませずに手を動かさせる・全画面にスキップ・下に3点のステップ表示で現在地は幅18pxの棒）: ① ようこそ「はじめる」 ② 覚えるのは、これ1つだけ — `settings.Hotkeys.QuickInput` のキーを出し、押されるまで「次へ」を無効（「押されるまで待っています」）、押されたら緑のチェック → 自動で③へ。押されたかは `HotkeyService.Intercept` で受ける（②を出している間だけ設定し、QuickInput なら true を返してクイック入力を開かせない。②を離れるとき・窓を閉じるとき必ず null に戻す）。`StateOf(HotkeyAction.QuickInput)` が `Registered` でなければその旨と「キーが効かない・別のキーにしたい」→ `ShellService.OpenSettings("shortcuts")`（`StatesChanged` で出し直し、新しいキーで待つ） ③ 日付も、そのまま書けます — 入力欄（`ImeGuard` 必須）に打つと `QuickInputParser.Parse` で読み取った日付の語をアクセント色にし、下に「9/23（水）と読み取りました」、その下に小さく「読み取れなくても、入力が消えることはありません」。Enter で `Parse(text).ToRequest()` → `ITaskRepository.AddAsync` ＋ `UndoService.Record`（トーストは出さない）→ 閉じる。`?` でショートカット一覧が出ることだけ一言添える。アカウント登録・機能紹介は入れない（UI 設計書 24.4）
- 確認の入口（波4-L で実装済み。開発用フォルダだけ）: `TASKDECK_DEV_FIRSTRUN=1`〜`3`（毎回初回起動を出す。2・3 はその画面から始める。開発用フォルダではホットキーを登録しないので②から先へ進めないため）、`TASKDECK_DEV_SPLASH=<秒数>`／`reduce`（起動後に `<データフォルダ>\dev-splash\` へ動きの途中（50・150・250・325・400ms）・動き終わり（等倍と150%）・動きを減らす設定の絵を書き出し、本物の起動画面を指定の秒数だけ出して閉じる）。②で本物のホットキーを押す流れはテストでだけ確かめてある
- `FirstRunWindow` のコンストラクタは `(FirstRunService, FirstRunViewModel, ShellService)`（DI からだけ作る）。タグラインは「思いついた速さで、片付ける」（モックアップの文言）

**M: アイコン**
- 形の正本は `docs/mockups/Brand.dc.html` の SVG で、それを写した `Resources/Brand/AppIcon.xaml`（256px の座標、波4-M で済み）。キー: `AppIconFrontCardGeometry` `AppIconMiddleCardGeometry` `AppIconBackCardGeometry`（どれも角丸四角をそのまま重ねる。前面が不透明なので下の2枚の下半分は隠れる。フェードは層ごとではなくアイコン全体にかける）`AppIconCheckGeometry`（線で描く。長さ約147.2）`AppIconCheckThickness` `AppIconColor` `AppIconFrontBrush` `AppIconMiddleBrush` `AppIconBackBrush` `AppIconCheckBrush` `AppIconImage`（完成形の DrawingImage）、大きさごとの絵 `AppIcon128Image` `AppIcon48Image` `AppIcon32Image` `AppIcon16Image`（その大きさの画素の座標で描いたもの。等倍で使う）
- `.ico` は `src/TaskDeck.App/Assets/TaskDeck.ico`（256/128/48/32/16）。UI 設計書 23.2 のとおりサイズごとに別の絵として描く（256・128 は3層で線幅24、48 は2層で線幅26、32 は2層で線幅30・角丸を少し大きく、16 は層を捨てて角丸四角とチェックだけで線幅34。線幅は 256 換算）。書き出しは `tools/IconGen`（WPF で描いて PNG にし ICO に詰める。`dotnet run --project tools/IconGen` で作り直せる。ソリューションには入れない・パッケージを足さない）。README に載せる PNG（例 `Assets/icon-256.png`）も置いてよい
- トレイ（UI 設計書 23.3）: `TrayIconRenderer` の絵をアプリのアイコンと同じチェックの形に揃える（ライトのタスクバーは黒・ダークは白の単色。バッジと停止中の表現は今のまま）。確認は `TASKDECK_DEV_RESIDENCY=trayicons` で `<データフォルダ>\dev-tray\` に書き出される絵を見る
- `.ico` は `ApplicationIcon` で exe に付けてある（窓は `Icon` を指定しなければ exe のアイコンを使う）。`System.Drawing.Icon` は 256 を選べない（幅0を256と読まない。WPF・シェルは読める）

## 6. 見た目の部品

- 色: `Resources/Themes/Light.xaml` と `Dark.xaml`（同じキー）。**必ず `DynamicResource`**。例 `{DynamicResource SurfaceContentBrush}` `{DynamicResource TextSecondaryBrush}` `{DynamicResource AccentDefaultBrush}` `{DynamicResource StateOverdueBrush}` `{DynamicResource PriorityHighBrush}`
- 寸法: `Resources/Common/Metrics.xaml`（`TaskRowHeight` `NavItemHeight` `RadiusRow` `PanePadding` など、`StaticResource`）
- 書体: `Resources/Common/Typography.xaml`（`TextTitleStyle` `TextBodyStyle` `TextCaptionStyle` `TabularCaptionStyle` など）
- 時間: `Resources/Common/Durations.xaml`（`DurationHover` `DurationComplete` など）
- アイコン: `Resources/Icons.xaml`（`IconToday` `IconCalendar` `IconList` `IconCheck` `IconTrash` `IconSearch` `IconSettings` `IconRepeat` `IconMenu` `IconAdd` `IconWarning` `IconBell` `IconReminder` `IconSort` `IconTemplate` `IconChart` `IconFocus` `IconClose` `IconFilter` `IconUndo` ほか59種）。`<Path Style="{StaticResource IconPathStyle}" Data="{StaticResource IconToday}" />`、大きさは Viewbox で変える
- ボタン: `PrimaryButtonStyle` `StandardButtonStyle` `TextButtonStyle` `DangerButtonStyle` `IconButtonStyle`。チェック: `TaskCheckBoxStyle` `SubtaskCheckBoxStyle`。入力: `InputTextBoxStyle` `InlineTextBoxStyle`。浮かせる面: `PopupShadowStyle` ＋ `PopupSurfaceStyle`
- キーボード操作時だけ出す 2px のフォーカス枠: `FocusVisualStyle="{StaticResource AppFocusVisualStyle}"`
- 色（波1-D で追加）: `TextOnDangerBrush`（危険ボタンの文字）、`PopupShadowBrush`（浮かせる面の影）
- プロジェクト色: `ProjectPalette.ForTheme(project.ColorHex, themeService.IsDark)`。コンバータなどで作った色は `ThemeService.ThemeChanged` で作り直す
- アニメーション: `ThemeService.ReduceMotion`（設定が null なら OS の設定）が true なら動きを省く。変わったら `ReduceMotionChanged`
- 窓の地: WPF-UI の FluentWindow は作成時とテーマ切替時に固定色を Background に入れるので、`ThemeService` が Surface.Window の参照に戻している。窓の背景を自分で指定するなら必ず `DynamicResource`

### 6.1 WPF と確認作業の落とし穴（担当の報告から）

- WPF-UI の `TitleBar` に部品を置くときは `Header` / `CenterContent` / `TrailingContent` の差し込み口に入れる。それ以外に重ねるとドラッグ領域として扱われ、押せない
- WPF-UI の `ListBox` は `Padding` を無視する。余白は `ItemContainerStyle` か外側の Border で付ける
- `StaysOpen=False` の Popup の中で ComboBox や入れ子の Popup を使うと、それを閉じた瞬間に外側の Popup まで閉じる。チップやリストで代える
- 裏にある窓から開いた Popup は、パネルがフォーカスを取ると開いた直後に閉じる（人がマウスで押すときは窓が前面なので起きない）
- フォーカスの見た目は `IsKeyboardFocused` のトリガーではなく `FocusVisualStyle`（`AppFocusVisualStyle`）で付ける。トリガーだとマウス操作の後や、開いた直後にパネルがフォーカスを取ったときにも出て、選択と見分けがつかない
- UI Automation: 所有される窓（設定・ダイアログ）はデスクトップ直下ではなくオーナーの子に出る。TextBlock に `AutomationProperties.Name` を付けると本文が読めない（読みたい欄は AutomationId で）。ComboBox の項目名は項目の `ToString()`
- スクリーンショットは PrintWindow で撮る（前面を奪わない）。Popup の透明な余白は黒く写る。キー入力の送信（SendKeys など）は依頼者の作業中の窓に打ち込む恐れがあるので使わない

## 7. テスト

- xUnit の `Assert` だけ。モックは NSubstitute
- 時計は `new FixedClock(utc)` か `FixedClock.AtLocal(2026, 9, 22, 10, 0)`（JST 固定）。期待値の UTC は `FixedClock.LocalToUtc(...)`
- DB は `new TestDatabase(clock)`（インメモリ SQLite に本物のマイグレーション）。`IDbContextFactory` として渡せる
- 設定は `new FakeSettingsStore()`、祝日は `new FixedHolidays(dates...)`
- 名前は `対象クラス名Tests`、メソッドは `対象_条件_期待結果`
- 外部APIを実際に叩かない

## 8. コマンド

```powershell
dotnet build TaskDeck.slnx
dotnet test TaskDeck.slnx
# 開発用のデータフォルダで起動（本番の %LOCALAPPDATA%\TaskDeck を汚さない）
$env:TASKDECK_DATA_DIR = "$PWD\.devdata\mine"; dotnet run --project src/TaskDeck.App
```
