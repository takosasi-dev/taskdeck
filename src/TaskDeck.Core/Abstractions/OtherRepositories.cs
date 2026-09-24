using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;

namespace TaskDeck.Core.Abstractions;

/// <summary>プロジェクト。削除すると所属タスクは「プロジェクトなし」に移る（タスクは消えない F-042）。</summary>
public interface IProjectRepository
{
    /// <summary>削除済みを除く。SortOrder 順。</summary>
    Task<IReadOnlyList<Project>> GetAllAsync(bool includeArchived = false, CancellationToken ct = default);

    Task<Project?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>色を省略すると ProjectPalette の6色から未使用のものを順に割り当てる。</summary>
    Task<Project> AddAsync(string name, string? colorHex = null, CancellationToken ct = default);

    /// <summary>名前（大文字小文字を無視、削除済みを除く）で引き、無ければ作る。</summary>
    Task<Project> GetOrCreateAsync(string name, CancellationToken ct = default);

    /// <summary>mutate で変えてよいのは Name, ColorHex, IconKey, IsArchived。</summary>
    Task UpdateAsync(Guid id, Action<Project> mutate, CancellationToken ct = default);

    Task ReorderAsync(Guid id, double sortOrder, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>タグ。名前は NFKC で正規化し、先頭の # を除く。大文字小文字を区別せず一意。</summary>
public interface ITagRepository
{
    /// <summary>削除済みを除く。名前順。</summary>
    Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default);

    Task<Tag> GetOrCreateAsync(string name, CancellationToken ct = default);

    /// <summary>同名（大文字小文字無視）が既にあれば Fail。全タスクに反映される（タグは Id で結ばれているため）。</summary>
    Task<OperationResult> RenameAsync(Guid id, string newName, CancellationToken ct = default);

    Task UpdateColorAsync(Guid id, string colorHex, CancellationToken ct = default);

    /// <summary>タグと、その TaskTag をソフトデリートする。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>どの未削除タスクにも付いていないタグを削除（F-045）。消した件数。</summary>
    Task<int> DeleteUnusedAsync(CancellationToken ct = default);
}

public sealed record TemplateSummary(TaskTemplate Template, int ItemCount);

public sealed record TemplateWithItems(TaskTemplate Template, IReadOnlyList<TaskTemplateItem> Items);

/// <summary>テンプレート（設計書 3.8 / 4.5）。</summary>
public interface ITemplateRepository
{
    /// <summary>削除済みを除く。UseCount 降順 → SortOrder 順。</summary>
    Task<IReadOnlyList<TemplateSummary>> GetAllAsync(CancellationToken ct = default);

    /// <summary>項目は Depth → SortOrder 順。</summary>
    Task<TemplateWithItems?> GetWithItemsAsync(Guid id, CancellationToken ct = default);

    /// <summary>テンプレートを丸ごと保存（新規なら追加、既存なら値と項目を置き換え。消えた項目はソフトデリート）。</summary>
    Task<TaskTemplate> SaveAsync(TaskTemplate template, IReadOnlyList<TaskTemplateItem> items, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>展開計画を1トランザクションで書き込み、UseCount++ と LastUsedAt=現在 も更新する。</summary>
    Task<TaskMutationResult> ExpandAsync(ExpansionPlan plan, CancellationToken ct = default);

    /// <summary>
    /// 既存タスク群からテンプレートを作る（F-152）。期限は anchorDate からの相対日数、時刻ありなら DueTime に。
    /// 子孫も含める。3階層を超える部分は切り捨て、そのとき Error に警告文を入れず Succeeded=true で返す。
    /// </summary>
    Task<OperationResult<TaskTemplate>> CreateFromTasksAsync(string name, IReadOnlyList<Guid> rootTaskIds, DateOnly anchorDate, CancellationToken ct = default);

    /// <summary>初期テンプレート3件（週次レビュー・買い物リスト・新しい課題に着手）を、まだ投入していなければ入れる。入れたら true。</summary>
    Task<bool> SeedDefaultsAsync(CancellationToken ct = default);
}

/// <summary>端末ローカルの状態（キーと値）。キーは AppStateKeys。</summary>
public interface IAppStateRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>null を渡すとキーを消す。</summary>
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

public static class AppStateKeys
{
    public const string LastVacuumDate = "maintenance.lastVacuumDate";
    public const string SampleDataSeeded = "seed.sampleTasks";
    public const string TemplatesSeeded = "seed.templates";
    public const string LastOverdueNoticeDate = "notify.lastOverdueNoticeDate";
    public const string LastDailySummaryDate = "notify.lastDailySummaryDate";
    public const string PaletteUsage = "palette.usage";
    public const string RecentTaskIds = "palette.recentTasks";
    public const string QuickInputHistory = "quickInput.history";
    public const string LastUpdateCheck = "update.lastCheck";
    public const string LatestKnownVersion = "update.latestVersion";
    public const string HolidayFetchedYears = "external.holidayYears";
    public const string WeatherLocationKey = "external.weatherLocation";
    /// <summary>使い捨てリストの全件（JSON）。タスクとは別物なので、一覧・検索・振り返り・通知・エクスポートには出ない。</summary>
    public const string ScratchLists = "scratch.lists";
    /// <summary>使い捨てリストを最後に出した場所（メイン画面か小窓か）と小窓の位置・大きさ（JSON）。</summary>
    public const string ScratchWindow = "scratch.window";
    /// <summary>初回起動の3画面（S-15）を終えた（スキップ・× を含む）日付（yyyy-MM-dd）。無ければ次に窓を出す起動で出す。</summary>
    public const string FirstRunCompleted = "onboarding.completed";
}
