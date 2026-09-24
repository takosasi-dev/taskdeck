using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskDeck.App;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Scratch;
using TaskDeck.App.Views.Templates;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Tests.Scratch;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Templates;

/// <summary>テンプレート画面の ViewModel。リポジトリはモック、展開計画は本物の TemplateExpander。時計は 2026-09-22（火）10:00 JST。</summary>
public class TemplatesViewModelTests
{
    private static readonly FixedClock Clock = FixedClock.AtLocal(2026, 9, 22, 10, 0);

    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly IProjectRepository _projects = Substitute.For<IProjectRepository>();
    private readonly ITagRepository _tags = Substitute.For<ITagRepository>();
    private readonly UndoStack _undoStack = new();
    private readonly ShellService _shell;
    private readonly TemplatesViewModel _vm;

    public TemplatesViewModelTests()
    {
        _projects.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Project>>([]));
        _tags.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Tag>>([]));
        _shell = new ShellService(Substitute.For<IServiceProvider>(), Clock, NullLogger<ShellService>.Instance);
        _vm = new TemplatesViewModel(
            _templates,
            _tasks,
            _projects,
            _tags,
            new TemplateExpander(Clock),
            new UndoService(_tasks, _undoStack, NullLogger<UndoService>.Instance),
            _shell,
            Clock,
            new AppPaths(@"C:\TaskDeckTests", IsDevelopment: false),
            NewScratchPresenter(),
            NullLogger<TemplatesViewModel>.Instance);
    }

    /// <summary>使い捨てリストの開き方（このテストでは使わない。保存先はメモリ）。</summary>
    private ScratchPresenter NewScratchPresenter()
    {
        var state = new MemoryAppState();
        return new ScratchPresenter(new ScratchStore(state, Clock), state, _templates, _shell, Substitute.For<IServiceProvider>(), NullLogger<ScratchPresenter>.Instance);
    }

    private static TaskTemplate Template(string name, int useCount = 0, DateTime? lastUsedAt = null, Guid? projectId = null, string? anchorLabel = null) =>
        new() { Name = name, UseCount = useCount, LastUsedAt = lastUsedAt, DefaultProjectId = projectId, AnchorLabel = anchorLabel };

    private static TaskTemplateItem Item(
        TaskTemplate template,
        string title,
        int? offset = 0,
        TaskTemplateItem? parent = null,
        double sortOrder = 1024,
        TimeOnly? time = null) => new()
    {
        TemplateId = template.Id,
        Title = title,
        DueOffsetDays = offset,
        ParentItemId = parent?.Id,
        Depth = parent is null ? 0 : parent.Depth + 1,
        SortOrder = sortOrder,
        DueTime = time,
    };

    /// <summary>リポジトリに並べる（GetAllAsync は渡した順＝使用回数の多い順として返す）。</summary>
    private void Given(params (TaskTemplate Template, TaskTemplateItem[] Items)[] templates)
    {
        _templates.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TemplateSummary>>([.. templates.Select(t => new TemplateSummary(t.Template, t.Items.Length))]));
        foreach (var (template, items) in templates)
        {
            _templates.GetWithItemsAsync(template.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TemplateWithItems?>(new TemplateWithItems(template, items)));
        }
    }

    private ExpansionRow Row(string title) => _vm.Rows.Single(r => r.Title == title);

    private static TaskMutationResult Created(int count) =>
        new(new ChangeSet([], [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())], []), [], []);

    [Fact]
    public async Task LoadAsync_使用回数の多い順_上位2件をよく使うにまとめて先頭を選ぶ()
    {
        var often = Template("週次レビュー", useCount: 12, lastUsedAt: Clock.UtcNow.AddDays(-8));
        var sometimes = Template("出張の準備", useCount: 4, lastUsedAt: Clock.UtcNow.AddDays(-90));
        var once = Template("面接の前日準備", useCount: 1, lastUsedAt: Clock.UtcNow);
        var never = Template("PC の初期設定");
        Given((often, [Item(often, "a"), Item(often, "b")]), (sometimes, [Item(sometimes, "a")]), (once, []), (never, []));

        await _vm.LoadAsync();

        Assert.Equal(["週次レビュー", "出張の準備", "面接の前日準備", "PC の初期設定"], _vm.Items.Select(i => i.Name));
        Assert.Equal(
            [TemplatesViewModel.FrequentGroup, TemplatesViewModel.FrequentGroup, TemplatesViewModel.AllGroup, TemplatesViewModel.AllGroup],
            _vm.Items.Select(i => i.Group));
        Assert.Equal("2 件 ・ 先週使用", _vm.Items[0].Summary);
        Assert.Equal("12回", _vm.Items[0].UseCountText);
        Assert.Equal("1 件 ・ 3か月前", _vm.Items[1].Summary);
        Assert.Null(_vm.Items[3].UseCountText);
        Assert.Equal(often.Id, _vm.Selected?.Id);
        Assert.Equal(TemplatesMode.Preview, _vm.Mode);
        Assert.Equal("週次レビュー", _vm.TemplateName);
    }

    [Fact]
    public async Task LoadAsync_テンプレートがない_空の表示にする()
    {
        Given();

        await _vm.LoadAsync();

        Assert.Empty(_vm.Items);
        Assert.Equal(TemplatesMode.Empty, _vm.Mode);
        Assert.False(_vm.CanExpand);
    }

    [Fact]
    public async Task LoadAsync_読み込みに失敗_短く知らせる()
    {
        _templates.GetAllAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new SqliteException("disk I/O error", 10));

        await _vm.LoadAsync();

        Assert.Equal("テンプレートを読み込めませんでした（ログに記録しました）", _vm.Notice);
    }

    [Fact]
    public async Task SelectAsync_基準日の呼び名_見出しに添えて今日を基準にする()
    {
        var trip = Template("出張の準備", anchorLabel: "出発日");
        Given((trip, [Item(trip, "荷造りをする", -1, time: new TimeOnly(20, 0))]));

        await _vm.LoadAsync();

        Assert.Equal("基準日（出発日）", _vm.AnchorCaption);
        Assert.Equal(new DateOnly(2026, 9, 22), _vm.AnchorDate);
        Assert.Equal("9月22日（火）", _vm.AnchorDateText);
        Assert.Equal("まだ使っていません", _vm.LastUsedText);
    }

    [Fact]
    public async Task Rows_親を外す_子孫も外れて作られる件数から除かれる()
    {
        var shopping = Template("買い物リスト");
        var food = Item(shopping, "食料品", null, sortOrder: 1024);
        var milk = Item(shopping, "牛乳", null, food, 1024);
        var eggs = Item(shopping, "卵", null, food, 2048);
        var daily = Item(shopping, "日用品", null, sortOrder: 2048);
        Given((shopping, [food, daily, milk, eggs]));
        await _vm.LoadAsync();

        Row("食料品").IsChecked = false;

        Assert.Equal(["食料品", "牛乳", "卵", "日用品"], _vm.Rows.Select(r => r.Title));   // 親の直後に子
        Assert.All([Row("牛乳"), Row("卵")], r => Assert.True(r.IsParentExcluded));
        Assert.All([Row("牛乳"), Row("卵")], r => Assert.False(r.IsChecked));
        Assert.Equal(1, _vm.CreateCount);
        Assert.Equal("1 件を作る", _vm.ExpandText);
    }

    [Fact]
    public async Task Rows_子だけ外す_親とほかの子は作られる()
    {
        var shopping = Template("買い物リスト");
        var food = Item(shopping, "食料品", null);
        var milk = Item(shopping, "牛乳", null, food, 1024);
        var eggs = Item(shopping, "卵", null, food, 2048);
        Given((shopping, [food, milk, eggs]));
        await _vm.LoadAsync();

        Row("牛乳").IsChecked = false;

        Assert.Equal(2, _vm.CreateCount);
        Assert.False(Row("卵").IsParentExcluded);
        Assert.True(Row("卵").IsChecked);
    }

    [Fact]
    public async Task PastCount_基準日より前の期限_件数と日付を警告し今日に寄せて見せる()
    {
        var issue = Template("新しい課題に着手");
        Given((issue, [Item(issue, "資料を集める", -7, sortOrder: 1024), Item(issue, "範囲を決める", -1, sortOrder: 2048), Item(issue, "着手", 0, sortOrder: 3072), Item(issue, "共有", 3, sortOrder: 4096)]));

        await _vm.LoadAsync();

        Assert.Equal(2, _vm.PastCount);
        Assert.True(_vm.HasPast);
        Assert.Equal("2 件の期限が過去の日付（9/15・9/21）になります。", _vm.PastWarningText);
        Assert.Equal("過去になるぶんは今日（9/22）にまとめる", _vm.PullPastText);
        Assert.True(_vm.PullPastToToday);   // 既定 ON（UI 設計書 20.5）
        Assert.Equal("9/22（火）今日", Row("資料を集める").DateText);
        Assert.True(Row("資料を集める").IsPast);
        Assert.Equal("7日前", Row("資料を集める").OffsetText);
        Assert.Equal("9/22（火）今日", Row("着手").DateText);
        Assert.Equal("9/25（金）", Row("共有").DateText);
    }

    [Fact]
    public async Task PullPastToToday_切る_元の日付に過去と添える()
    {
        var issue = Template("新しい課題に着手");
        Given((issue, [Item(issue, "資料を集める", -7)]));
        await _vm.LoadAsync();

        _vm.PullPastToToday = false;

        Assert.Equal("9/15（火）過去", Row("資料を集める").DateText);
        Assert.False(Row("資料を集める").IsToday);
    }

    [Fact]
    public async Task AnchorDate_変える_実日付と過去の件数を計算し直す()
    {
        var trip = Template("出張の準備");
        Given((trip, [Item(trip, "航空券", -7), Item(trip, "荷造り", -1, time: new TimeOnly(20, 0), sortOrder: 2048)]));
        await _vm.LoadAsync();

        _vm.AnchorDate = new DateOnly(2026, 10, 2);

        Assert.Equal(0, _vm.PastCount);
        Assert.False(_vm.HasPast);
        Assert.Equal("9/25（金）", Row("航空券").DateText);
        Assert.Equal("10/1（木） 20:00", Row("荷造り").DateText);
        Assert.Equal("10月2日（金）", _vm.AnchorDateText);
    }

    [Fact]
    public async Task ExpandAsync_展開する_計画を書き込み取り消しを積んで閉じる()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る", sortOrder: 1024), Item(weekly, "整理する", sortOrder: 2048), Item(weekly, "決める", sortOrder: 3072)]));
        ExpansionPlan? written = null;
        _templates.ExpandAsync(Arg.Do<ExpansionPlan>(p => written = p), Arg.Any<CancellationToken>()).Returns(Created(2));
        var closed = false;
        await _vm.LoadAsync();
        _vm.CloseRequested += (_, _) => closed = true;
        Row("整理する").IsChecked = false;

        await _vm.ExpandCommand.ExecuteAsync(null);

        Assert.NotNull(written);
        Assert.Equal(weekly.Id, written.TemplateId);
        Assert.Equal(["振り返る", "決める"], written.Tasks.Select(t => t.Title));
        Assert.Equal(Clock.LocalDayStartUtc(new DateOnly(2026, 9, 22)), written.Tasks[0].DueAt);
        var entry = Assert.IsType<UndoEntry>(_undoStack.Peek());
        Assert.Equal("「週次レビュー」から 2 件を作りました", entry.Label);
        Assert.Equal(UndoKind.Bulk, entry.Kind);
        Assert.True(closed);
    }

    [Fact]
    public async Task ExpandAsync_展開する_トーストを頼む()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る")]));
        _templates.ExpandAsync(Arg.Any<ExpansionPlan>(), Arg.Any<CancellationToken>()).Returns(Created(1));
        var undo = new UndoService(_tasks, _undoStack, NullLogger<UndoService>.Instance);
        string? toast = null;
        undo.ToastRequested += (_, e) => toast = e.Message;
        var vm = new TemplatesViewModel(
            _templates, _tasks, _projects, _tags, new TemplateExpander(Clock), undo, _shell, Clock,
            new AppPaths(@"C:\TaskDeckTests", IsDevelopment: false), NewScratchPresenter(), NullLogger<TemplatesViewModel>.Instance);
        await vm.LoadAsync();

        await vm.ExpandCommand.ExecuteAsync(null);

        Assert.Equal("「週次レビュー」から 1 件を作りました", toast);
    }

    [Fact]
    public async Task ExpandAsync_書き込みに失敗_知らせて閉じず取り消しも積まない()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る")]));
        _templates.ExpandAsync(Arg.Any<ExpansionPlan>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SqliteException("database is locked", 5));
        var closed = false;
        await _vm.LoadAsync();
        _vm.CloseRequested += (_, _) => closed = true;

        await _vm.ExpandCommand.ExecuteAsync(null);

        Assert.Equal("タスクを作れませんでした（ログに記録しました）", _vm.Notice);
        Assert.False(closed);
        Assert.Equal(0, _undoStack.Count);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task ProjectId_テンプレートの既定_そのまま入れる先になる()
    {
        var projectId = Guid.CreateVersion7();
        var weekly = Template("週次レビュー", projectId: projectId);
        Given((weekly, [Item(weekly, "振り返る")]));
        ExpansionPlan? written = null;
        _templates.ExpandAsync(Arg.Do<ExpansionPlan>(p => written = p), Arg.Any<CancellationToken>()).Returns(Created(1));
        await _vm.LoadAsync();

        await _vm.ExpandCommand.ExecuteAsync(null);

        Assert.Equal(projectId, _vm.ProjectId);
        Assert.Equal(projectId, written!.Tasks[0].ProjectId);
    }

    [Fact]
    public async Task ProjectId_なしを選ぶ_テンプレートの既定に戻らない()
    {
        var weekly = Template("週次レビュー", projectId: Guid.CreateVersion7());
        Given((weekly, [Item(weekly, "振り返る")]));
        ExpansionPlan? written = null;
        _templates.ExpandAsync(Arg.Do<ExpansionPlan>(p => written = p), Arg.Any<CancellationToken>()).Returns(Created(1));
        await _vm.LoadAsync();

        await _vm.PickProjectAsync(null);
        await _vm.ExpandCommand.ExecuteAsync(null);

        Assert.Null(written!.Tasks[0].ProjectId);
        Assert.Equal("なし", _vm.ProjectName);
    }

    [Fact]
    public async Task ExpansionTags_タグを選び直す_全項目をそのタグだけで上書きする()
    {
        var work = new Tag { Name = "仕事" };
        var shop = new Tag { Name = "買い物" };
        _tags.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Tag>>([work, shop]));
        var weekly = Template("週次レビュー");
        weekly.DefaultTagIds = [work.Id];
        var item = Item(weekly, "振り返る");
        item.TagIds = [shop.Id];
        Given((weekly, [item]));
        var plans = new List<ExpansionPlan>();
        _templates.ExpandAsync(Arg.Do<ExpansionPlan>(plans.Add), Arg.Any<CancellationToken>()).Returns(Created(1));
        await _vm.LoadAsync();
        Assert.Equal("テンプレートどおり", _vm.TagsSummary);
        await _vm.ExpandCommand.ExecuteAsync(null);

        _vm.ExpansionTags.Choices.Single(c => c.Id == work.Id).IsSelected = false;
        _vm.ExpansionTags.Choices.Single(c => c.Id == shop.Id).IsSelected = true;
        await _vm.ExpandCommand.ExecuteAsync(null);
        _vm.ResetTagsCommand.Execute(null);

        Assert.Equal([work.Id, shop.Id], plans[0].Tasks[0].TagIds);   // 既定＋項目のタグ
        Assert.Equal([shop.Id], plans[1].Tasks[0].TagIds);            // 選んだタグだけ
        Assert.True(_vm.UseTemplateTags);
        Assert.Equal("テンプレートどおり", _vm.TagsSummary);
    }

    [Fact]
    public async Task StartFromTasks_選択が空_メイン画面で選ぶよう案内する()
    {
        Given();
        await _vm.LoadAsync();
        _shell.SelectedTaskIds = [];

        await _vm.StartFromTasksCommand.ExecuteAsync(null);

        Assert.Equal("メイン画面でタスクを選んでから押してください", _vm.Notice);
        Assert.Equal(TemplatesMode.Empty, _vm.Mode);
        await _tasks.DidNotReceiveWithAnyArgs().GetByIdsAsync(default!, default);
    }

    [Fact]
    public async Task StartFromTasks_選択あり_名前と一番早い期限を基準日に入れて聞く()
    {
        Given();
        await _vm.LoadAsync();
        var meeting = new TaskItem { Title = "会議の準備", DueAt = Clock.LocalDayStartUtc(new DateOnly(2026, 9, 25)) };
        var print = new TaskItem { Title = "資料を印刷する", DueAt = FixedClock.LocalToUtc(2026, 9, 24, 15, 0), DueHasTime = true };
        _shell.SelectedTaskIds = [meeting.Id, print.Id];
        _tasks.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskItem>>([print, meeting]));

        await _vm.StartFromTasksCommand.ExecuteAsync(null);

        Assert.Equal(TemplatesMode.FromTasks, _vm.Mode);
        Assert.Equal("会議の準備", _vm.FromTasksName);   // 選んだ順の先頭
        Assert.Equal(new DateOnly(2026, 9, 24), _vm.FromTasksAnchor);
        Assert.Equal("「会議の準備」ほか 1 件（サブタスクを含む）をテンプレートにします。", _vm.FromTasksSummary);
        Assert.False(_vm.IsListEnabled);
    }

    [Fact]
    public async Task CreateFromTasks_作れた_作ったテンプレートを選んで編集を開く()
    {
        Given();
        await _vm.LoadAsync();
        var task = new TaskItem { Title = "会議の準備" };
        _shell.SelectedTaskIds = [task.Id];
        _tasks.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskItem>>([task]));
        await _vm.StartFromTasksCommand.ExecuteAsync(null);
        var created = Template("会議の手順");
        _templates.CreateFromTasksAsync("会議の手順", Arg.Any<IReadOnlyList<Guid>>(), new DateOnly(2026, 9, 22), Arg.Any<CancellationToken>())
            .Returns(OperationResult<TaskTemplate>.Success(created));
        Given((created, [Item(created, "会議の準備")]));

        _vm.FromTasksName = "会議の手順";
        await _vm.CreateFromTasksCommand.ExecuteAsync(null);

        await _templates.Received(1).CreateFromTasksAsync(
            "会議の手順", Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(new[] { task.Id })), new DateOnly(2026, 9, 22), Arg.Any<CancellationToken>());
        Assert.Equal(created.Id, _vm.Selected?.Id);
        Assert.Equal(TemplatesMode.Editor, _vm.Mode);
        Assert.Equal("会議の手順", _vm.Editor?.Name);
        Assert.False(_vm.Editor?.IsNew);
    }

    [Fact]
    public async Task CreateFromTasks_作れない_理由を出してそのまま待つ()
    {
        Given();
        await _vm.LoadAsync();
        var task = new TaskItem { Title = "会議の準備" };
        _shell.SelectedTaskIds = [task.Id];
        _tasks.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskItem>>([task]));
        _templates.CreateFromTasksAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(OperationResult<TaskTemplate>.Fail("テンプレート名を入力してください。"));
        await _vm.StartFromTasksCommand.ExecuteAsync(null);

        _vm.FromTasksName = "  ";
        await _vm.CreateFromTasksCommand.ExecuteAsync(null);

        Assert.Equal("テンプレート名を入力してください。", _vm.FromTasksError);
        Assert.Equal(TemplatesMode.FromTasks, _vm.Mode);
    }

    [Fact]
    public async Task SaveAsync_編集して保存_丸ごと保存して選び直す()
    {
        var weekly = Template("週次レビュー", useCount: 3);
        weekly.SortOrder = 4096;
        Given((weekly, [Item(weekly, "振り返る")]));
        _templates.SaveAsync(Arg.Any<TaskTemplate>(), Arg.Any<IReadOnlyList<TaskTemplateItem>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<TaskTemplate>()));
        await _vm.LoadAsync();
        _vm.EditCommand.Execute(null);

        _vm.Editor!.Name = "週次のふりかえり";
        _vm.Editor.AddItemCommand.Execute(null);
        _vm.Editor.SelectedItem!.Title = "来週の予定を見る";
        await _vm.SaveCommand.ExecuteAsync(null);

        await _templates.Received(1).SaveAsync(
            Arg.Is<TaskTemplate>(t => t.Id == weekly.Id && t.Name == "週次のふりかえり" && t.SortOrder == 4096),
            Arg.Is<IReadOnlyList<TaskTemplateItem>>(items => items.Select(i => i.Title).SequenceEqual(new[] { "振り返る", "来週の予定を見る" })),
            Arg.Any<CancellationToken>());
        Assert.Equal(TemplatesMode.Preview, _vm.Mode);
        Assert.Null(_vm.Editor);
    }

    [Fact]
    public async Task SaveAsync_保存に失敗_編集を続けられるよう理由を出す()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る")]));
        _templates.SaveAsync(Arg.Any<TaskTemplate>(), Arg.Any<IReadOnlyList<TaskTemplateItem>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("削除済みのテンプレートは保存できません。"));
        await _vm.LoadAsync();
        _vm.EditCommand.Execute(null);

        await _vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(TemplatesMode.Editor, _vm.Mode);
        Assert.Equal("保存できませんでした（ログに記録しました）", _vm.Editor?.Error);
    }

    [Fact]
    public async Task NewTemplate_新規作成_空の項目1つから始める()
    {
        Given();
        await _vm.LoadAsync();

        _vm.NewTemplateCommand.Execute(null);

        Assert.Equal(TemplatesMode.Editor, _vm.Mode);
        Assert.True(_vm.Editor?.IsNew);
        Assert.Single(_vm.Editor!.Items);
        Assert.Equal("新しいテンプレート", _vm.Editor.Heading);
    }

    [Fact]
    public async Task Delete_確認してから_削除して一覧を読み直す()
    {
        var weekly = Template("週次レビュー");
        var trip = Template("出張の準備");
        Given((weekly, [Item(weekly, "振り返る")]), (trip, [Item(trip, "荷造り")]));
        await _vm.LoadAsync();
        _vm.EditCommand.Execute(null);

        _vm.RequestDeleteCommand.Execute(null);
        Assert.True(_vm.IsConfirmingDelete);
        Given((trip, [Item(trip, "荷造り")]));
        await _vm.ConfirmDeleteCommand.ExecuteAsync(null);

        await _templates.Received(1).DeleteAsync(weekly.Id, Arg.Any<CancellationToken>());
        Assert.Equal(trip.Id, _vm.Selected?.Id);
        Assert.Equal(TemplatesMode.Preview, _vm.Mode);
        Assert.False(_vm.IsConfirmingDelete);
    }

    [Fact]
    public async Task GoBack_Escの段階_確認を閉じ編集をやめてから窓を閉じる()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る")]));
        await _vm.LoadAsync();
        _vm.EditCommand.Execute(null);
        _vm.RequestDeleteCommand.Execute(null);

        Assert.True(_vm.GoBack());
        Assert.False(_vm.IsConfirmingDelete);
        Assert.Equal(TemplatesMode.Editor, _vm.Mode);
        Assert.True(_vm.GoBack());
        Assert.Equal(TemplatesMode.Preview, _vm.Mode);
        Assert.False(_vm.GoBack());
    }

    [Fact]
    public async Task SearchText_全角で探す_名前の一部で絞る()
    {
        var aws = Template("AWS 課題");
        var weekly = Template("週次レビュー");
        Given((weekly, []), (aws, []));
        await _vm.LoadAsync();
        Assert.Equal(weekly.Id, _vm.Selected?.Id);

        _vm.SearchText = "ａｗｓ";

        Assert.Equal(["AWS 課題"], _vm.Items.Select(i => i.Name));
        Assert.Equal(aws.Id, _vm.Selected?.Id);   // 見えなくなったものの代わりに、見えている先頭を出す
        Assert.Equal("AWS 課題", _vm.TemplateName);
    }

    [Fact]
    public async Task RefreshListAsync_ほかでテンプレートが増える_一覧だけ読み直し入力中の値は保つ()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る", sortOrder: 1024), Item(weekly, "決める", sortOrder: 2048)]));
        await _vm.LoadAsync();
        _vm.AnchorDate = new DateOnly(2026, 10, 1);
        Row("決める").IsChecked = false;
        var trip = Template("出張の準備");
        var usedWeekly = Template("週次レビュー", useCount: 1, lastUsedAt: FixedClock.LocalToUtc(2026, 9, 22, 9, 0));
        usedWeekly.Id = weekly.Id;
        Given((usedWeekly, []), (trip, []));

        await _vm.RefreshListAsync();

        Assert.Equal(["週次レビュー", "出張の準備"], _vm.Items.Select(i => i.Name));
        Assert.Equal(weekly.Id, _vm.Selected?.Id);
        Assert.Equal(new DateOnly(2026, 10, 1), _vm.AnchorDate);
        Assert.False(Row("決める").IsChecked);
        Assert.Equal("前回 2026/09/22 に使用", _vm.LastUsedText);
        await _templates.Received(1).GetWithItemsAsync(weekly.Id, Arg.Any<CancellationToken>());   // 中身は読み直さない
    }

    [Fact]
    public async Task RefreshListAsync_出していたテンプレートが消えた_先頭を出し直す()
    {
        var weekly = Template("週次レビュー");
        var trip = Template("出張の準備");
        Given((weekly, [Item(weekly, "振り返る")]), (trip, [Item(trip, "荷造り")]));
        await _vm.LoadAsync();
        Given((trip, [Item(trip, "荷造り")]));

        await _vm.RefreshListAsync();

        Assert.Equal(trip.Id, _vm.Selected?.Id);
        Assert.Equal("出張の準備", _vm.TemplateName);
        Assert.Equal(TemplatesMode.Preview, _vm.Mode);
    }

    [Fact]
    public async Task RefreshListAsync_編集中_一覧だけ変えて編集は続ける()
    {
        var weekly = Template("週次レビュー");
        Given((weekly, [Item(weekly, "振り返る")]));
        await _vm.LoadAsync();
        _vm.EditCommand.Execute(null);
        _vm.Editor!.Name = "書きかけ";
        Given();

        await _vm.RefreshListAsync();

        Assert.Equal(TemplatesMode.Editor, _vm.Mode);
        Assert.Equal("書きかけ", _vm.Editor?.Name);
        Assert.Empty(_vm.Items);
    }

    [Fact]
    public async Task RefreshThemeColors_テーマが変わる_一覧を作り直しても選択と中身は保つ()
    {
        var weekly = Template("週次レビュー");
        var trip = Template("出張の準備");
        Given((weekly, [Item(weekly, "振り返る")]), (trip, [Item(trip, "荷造り")]));
        await _vm.LoadAsync();
        await _vm.SelectAsync(_vm.Items[1]);
        var before = _vm.Items[1];

        _vm.RefreshThemeColors();

        Assert.NotSame(before, _vm.Items[1]);   // 行を作り直す（色はそのときのテーマで作られる）
        Assert.Equal(trip.Id, _vm.Selected?.Id);
        Assert.Equal("出張の準備", _vm.TemplateName);
        await _templates.Received(1).GetWithItemsAsync(trip.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, "今日使用")]
    [InlineData(1, "昨日使用")]
    [InlineData(3, "3日前")]
    [InlineData(8, "先週使用")]
    [InlineData(20, "2週間前")]
    [InlineData(90, "3か月前")]
    [InlineData(400, "1年前")]
    public void UsedAgo_日数_短い言い方にする(int days, string expected)
    {
        var today = new DateOnly(2026, 9, 22);

        Assert.Equal(expected, TemplatesViewModel.UsedAgo(today.AddDays(-days), today));
    }
}
