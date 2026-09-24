using System.Text.Json;
using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Palette;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Palette;

public class CommandPaletteViewModelTests
{
    private readonly PaletteFixture _f = new();

    private async Task<CommandPaletteViewModel> OpenAsync(string text = "")
    {
        var palette = _f.CreatePalette();
        await palette.InitializeAsync();
        if (text.Length > 0)
        {
            await TypeAsync(palette, text);
        }
        return palette;
    }

    private static async Task TypeAsync(CommandPaletteViewModel palette, string text)
    {
        palette.SetText(text);
        await palette.RefreshAsync();
    }

    private static PaletteItem ItemLabeled(CommandPaletteViewModel palette, string label) =>
        palette.Rows.OfType<PaletteItem>().Single(i => i.Label == label);

    // ---- セクションと並び ----

    [Fact]
    public async Task RefreshAsync_TextWithoutSymbol_ShowsTasksThenCommandsThenMoves()
    {
        _f.AddTask("設定ファイルを直す");
        _f.AddProject("設定まわり");

        var palette = await OpenAsync("設定");

        var headers = palette.Rows.OfType<PaletteHeaderRow>().Select(h => h.Title).ToList();
        Assert.Equal(["タスク", "コマンド", "移動"], headers);
        Assert.False(palette.Rows.OfType<PaletteHeaderRow>().First().HasDivider);
        Assert.True(palette.Rows.OfType<PaletteHeaderRow>().Last().HasDivider);
        Assert.Equal("設定ファイルを直す", Assert.IsType<PaletteTaskItem>(palette.Selected).Title);
    }

    /// <summary>期限を1日ずつずらした「会議 1」〜「会議 N」（並びを期限で決めるため）。</summary>
    private void AddMeetings(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            _f.AddTask($"会議 {i}", new DateTime(2026, 9, 22 + i));
        }
    }

    [Fact]
    public async Task RefreshAsync_ManyMatches_ShowsThreePerSectionAndMoreRow()
    {
        AddMeetings(5);

        var palette = await OpenAsync("会議");

        var lines = PaletteFixture.Describe(palette.Rows);
        Assert.Equal(["# タスク", "会議 1", "会議 2", "会議 3", "さらに 2 件"], lines.Take(5));
        Assert.StartsWith("5 件のタスク", palette.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_MoreRow_ExpandsSectionAndSelectsNextItem()
    {
        AddMeetings(5);
        var palette = await OpenAsync("会議");

        await palette.ExecuteAsync(ItemLabeled(palette, "さらに 2 件"));

        var tasks = palette.Rows.OfType<PaletteTaskItem>().Select(t => t.Title).ToList();
        Assert.Equal(["会議 1", "会議 2", "会議 3", "会議 4", "会議 5"], tasks);
        Assert.Equal("会議 4", Assert.IsType<PaletteTaskItem>(palette.Selected).Title);
        _f.Host.DidNotReceiveWithAnyArgs().RevealTask(default);
    }

    [Fact]
    public async Task RefreshAsync_Tasks_ClosedLastThenPrefixThenNearestDue()
    {
        _f.AddTask("資料の会議", new DateTime(2026, 9, 23));
        _f.AddTask("会議の準備", new DateTime(2026, 9, 30));
        _f.AddTask("会議室を予約", new DateTime(2026, 9, 25));
        _f.AddTask("会議の議事録", status: TaskItemStatus.Completed);
        _f.AddTask("定例の会議", new DateTime(2026, 9, 20));

        var palette = await OpenAsync("会議");
        await palette.ExecuteAsync(ItemLabeled(palette, "さらに 2 件"));

        var tasks = palette.Rows.OfType<PaletteTaskItem>().Select(t => t.Title).ToList();
        Assert.Equal(["会議室を予約", "会議の準備", "定例の会議", "資料の会議", "会議の議事録"], tasks);
    }

    [Fact]
    public async Task RefreshAsync_TaskSearch_QueriesAllStatusesWithoutSubtaskContext()
    {
        var palette = await OpenAsync("請求");

        var query = _f.Queries[^1];
        Assert.Equal("請求", query.SearchText);
        Assert.Equal(TaskStatusFilter.All, query.Status);
        Assert.False(query.IncludeSubtasks);
    }

    [Fact]
    public async Task RefreshAsync_MatchedText_HighlightsInsideLabel()
    {
        _f.AddProject("業務改善");

        var palette = await OpenAsync("@業務");

        var item = Assert.Single(palette.Rows.OfType<PaletteItem>());
        Assert.Equal("プロジェクト「業務改善」を開く", item.Label);
        Assert.Equal([new TextPiece("プロジェクト「", false), new TextPiece("業務", true), new TextPiece("改善」を開く", false)], item.Pieces);
    }

    [Fact]
    public async Task RefreshAsync_LooseMatch_RanksBelowPrefixMatch()
    {
        _f.AddProject("業務改善");
        _f.AddProject("業改の相談");

        var palette = await OpenAsync("@業改");

        Assert.Equal(["# プロジェクト", "プロジェクト「業改の相談」を開く", "プロジェクト「業務改善」を開く"], PaletteFixture.Describe(palette.Rows));
    }

    [Fact]
    public async Task RefreshAsync_MovesSection_ProjectsThenTagsThenViews()
    {
        _f.AddTag("予定外");
        _f.AddProject("予定の整理");

        var palette = await OpenAsync("予定");

        var moves = palette.Rows.OfType<PaletteItem>()
            .Where(i => i.Kind is PaletteItemKind.Project or PaletteItemKind.Tag or PaletteItemKind.View)
            .Select(i => i.Label)
            .ToList();
        Assert.Equal(["プロジェクト「予定の整理」を開く", "タグ「#予定外」を開く", "「予定」を開く"], moves);
    }

    [Theory]
    [InlineData(">", "コマンド")]
    [InlineData("#", "タグ")]
    [InlineData("@", "プロジェクト")]
    [InlineData("/", "ビュー")]
    [InlineData("+", "テンプレート")]
    [InlineData("?", "ヘルプ")]
    public async Task RefreshAsync_Symbol_ShowsOnlyThatSection(string text, string header)
    {
        _f.AddTask("#仕事 の見直し");
        _f.AddProject("業務改善");
        _f.AddTag("仕事");
        _f.AddTemplate("週次レビュー", ("振り返る", 0));

        var palette = await OpenAsync(text);

        Assert.Equal(header, Assert.Single(palette.Rows.OfType<PaletteHeaderRow>()).Title);
        Assert.Empty(palette.Rows.OfType<PaletteTaskItem>());
        Assert.DoesNotContain(_f.Queries, q => q.HasSearch);
    }

    [Fact]
    public async Task RefreshAsync_CommandSymbol_ListsCommandsAndTemplatesByUsage()
    {
        _f.AddTemplate("週次レビュー", ("振り返る", 0));
        _f.AppState.Values[AppStateKeys.PaletteUsage] = JsonSerializer.Serialize(new[] { "command:toggletheme", "command:toggletheme", "command:backup" });

        var palette = await OpenAsync(">");

        var labels = palette.Rows.OfType<PaletteItem>().Select(i => i.Label).ToList();
        Assert.Equal(["テーマを切り替える", "バックアップを取る", "期限切れをまとめて今日へ繰り越す"], labels.Take(3));
        Assert.Contains("テンプレート「週次レビュー」から作る", labels);
    }

    [Fact]
    public async Task RefreshAsync_SameStrength_MoreUsedCommandFirst()
    {
        var palette = await OpenAsync(">設定:");
        var before = palette.Rows.OfType<PaletteItem>().Select(i => i.Label).First();
        _f.AppState.Values[AppStateKeys.PaletteUsage] = JsonSerializer.Serialize(new[] { "command:settings:data" });

        var reopened = await OpenAsync(">設定:");

        Assert.Equal("設定: 全般", before);
        Assert.Equal("設定: データ", reopened.Rows.OfType<PaletteItem>().Select(i => i.Label).First());
    }

    [Fact]
    public async Task InitializeAsync_EmptyInput_ShowsRecentTasksAndDefaultCommands()
    {
        var first = _f.AddTask("最初に開いた");
        var second = _f.AddTask("次に開いた");
        _f.AppState.Values[AppStateKeys.RecentTaskIds] = JsonSerializer.Serialize(new[] { second.Id, first.Id });

        var palette = await OpenAsync();

        Assert.Equal(
            ["# 最近開いたタスク", "次に開いた", "最初に開いた", "# よく使うコマンド", "期限切れをまとめて今日へ繰り越す", "テンプレートから作る", "フォーカスモードを始める"],
            PaletteFixture.Describe(palette.Rows));
        Assert.DoesNotContain(_f.Queries, q => q.HasSearch);
    }

    [Fact]
    public async Task InitializeAsync_BrokenUsageJson_StartsEmpty()
    {
        _f.AppState.Values[AppStateKeys.PaletteUsage] = "{壊れた";

        var palette = await OpenAsync();

        Assert.Contains(palette.Rows.OfType<PaletteItem>(), i => i.Label == "期限切れをまとめて今日へ繰り越す");
    }

    [Fact]
    public async Task RefreshAsync_TextTyped_OffersCreateRowFirstInCommands()
    {
        var palette = await OpenAsync("会議資料 明日 15:00 #仕事");

        var create = palette.Rows.OfType<PaletteItem>().First(i => i.Kind == PaletteItemKind.CreateTask);
        Assert.Equal("「会議資料」という名前でタスクを追加", create.Label);
        Assert.Equal("明日 15:00 #仕事", create.Detail);
        Assert.Equal("Ctrl+Enter", create.KeyHint);
    }

    // ---- キー操作 ----

    [Fact]
    public async Task MoveSelection_CrossesSectionsAndStopsAtEnds()
    {
        _f.AddTask("設定ファイル");
        var palette = await OpenAsync("設定");
        var entries = palette.Rows.OfType<PaletteEntry>().ToList();

        palette.MoveSelection(1);
        var second = palette.Selected;
        palette.MoveSelection(-5);

        Assert.Same(entries[1], second);
        Assert.IsType<PaletteItem>(second);
        Assert.Same(entries[0], palette.Selected);
        Assert.True(entries[0].IsSelected);
        Assert.False(entries[1].IsSelected);
    }

    [Fact]
    public async Task CanCompleteWithSpace_OnlyAfterArrowKeysOnTask()
    {
        _f.AddTask("会議資料");
        _f.AddTask("会議室");
        var palette = await OpenAsync("会議");

        var typed = palette.CanCompleteWithSpace;
        palette.MoveSelection(1);
        var moved = palette.CanCompleteWithSpace;
        palette.SetText("会議 ");

        Assert.False(typed);
        Assert.True(moved);
        Assert.False(palette.CanCompleteWithSpace);
    }

    [Fact]
    public async Task ClearInputAsync_FirstClearsThenAllowsClose()
    {
        var palette = await OpenAsync("会議");
        string? replaced = null;
        palette.TextReplaced += (_, text) => replaced = text;

        var first = await palette.ClearInputAsync();
        var second = await palette.ClearInputAsync();

        Assert.True(first);
        Assert.Equal("", replaced);
        Assert.Equal("", palette.Text);
        Assert.False(second);
    }

    [Fact]
    public async Task NarrowAsync_ProjectSelected_SearchesTasksInsideProject()
    {
        var project = _f.AddProject("業務改善");
        _f.AddTask("資料を集める", projectId: project.Id);
        _f.AddTask("資料を捨てる");
        var palette = await OpenAsync("@業務");

        var narrowed = await palette.NarrowAsync();
        await TypeAsync(palette, "資料");

        Assert.True(narrowed);
        Assert.Equal(project.Id, palette.Scope?.Id);
        Assert.Equal(project.Id, _f.Queries[^1].ProjectId);
        Assert.Equal(["# 「業務改善」のタスク", "資料を集める", "# コマンド", "「資料」という名前でタスクを追加"], PaletteFixture.Describe(palette.Rows));
    }

    [Fact]
    public async Task NarrowAsync_CommandSelected_DoesNothing()
    {
        var palette = await OpenAsync(">テーマ");

        Assert.False(await palette.NarrowAsync());
        Assert.Null(palette.Scope);
    }

    [Fact]
    public async Task RemoveScopeAsync_AfterNarrow_ReturnsToNormalSearch()
    {
        _f.AddTag("仕事");
        var palette = await OpenAsync("#仕事");
        await palette.NarrowAsync();

        Assert.True(await palette.RemoveScopeAsync());
        Assert.Null(palette.Scope);
        Assert.False(await palette.RemoveScopeAsync());
    }

    // ---- 実行 ----

    [Fact]
    public async Task ExecuteAsync_View_ClosesThenNavigatesAndRecordsUsage()
    {
        var palette = await OpenAsync("/予定");
        var closed = false;
        palette.CloseRequested += (_, _) => closed = true;

        await palette.ExecuteAsync(palette.Selected);

        Assert.True(closed);
        _f.Host.Received(1).NavigateTo(ViewKey.Upcoming);
        Assert.Contains("view:upcoming", JsonSerializer.Deserialize<List<string>>(_f.AppState.Values[AppStateKeys.PaletteUsage])!);
    }

    [Fact]
    public async Task ExecuteAsync_EnterBeforeSearchRan_UsesResultsForNewText()
    {
        var palette = await OpenAsync();
        palette.SetText("/予定");   // 入力停止の 100ms を待たずに Enter

        await palette.ExecuteAsync(null);

        _f.Host.Received(1).NavigateTo(ViewKey.Upcoming);
        await _f.Tasks.DidNotReceiveWithAnyArgs().CarryOverOverdueAsync();
    }

    [Fact]
    public async Task ExecuteAsync_Project_NavigatesToProjectView()
    {
        var project = _f.AddProject("業務改善");
        var palette = await OpenAsync("@業務");

        await palette.ExecuteAsync(palette.Selected);

        _f.Host.Received(1).NavigateTo(ViewKey.ForProject(project.Id));
    }

    [Fact]
    public async Task ExecuteAsync_Task_RevealsAndRemembersAsRecent()
    {
        var old = _f.AddTask("前に開いた");
        var task = _f.AddTask("会議資料");
        _f.AppState.Values[AppStateKeys.RecentTaskIds] = JsonSerializer.Serialize(new[] { old.Id });
        var palette = await OpenAsync("会議");

        await palette.ExecuteAsync(palette.Selected);

        _f.Host.Received(1).RevealTask(task.Id);
        Assert.Equal([task.Id, old.Id], JsonSerializer.Deserialize<List<Guid>>(_f.AppState.Values[AppStateKeys.RecentTaskIds])!);
    }

    [Theory]
    [InlineData(">通知", "notifications")]
    [InlineData(">設定を開く", null)]
    public async Task ExecuteAsync_SettingsCommand_OpensThatPage(string text, string? page)
    {
        var palette = await OpenAsync(text);

        await palette.ExecuteAsync(palette.Selected);

        _f.Host.Received(1).OpenSettings(page);
    }

    [Fact]
    public async Task ExecuteAsync_ShortcutsFromHelp_OpensShortcuts()
    {
        var palette = await OpenAsync("?");

        await palette.ExecuteAsync(palette.Selected);

        _f.Host.Received(1).OpenShortcuts();
    }

    [Fact]
    public async Task ExecuteAsync_PrefixRow_InsertsSymbolAndStaysOpen()
    {
        _f.AddTag("仕事");
        var palette = await OpenAsync("?");
        string? replaced = null;
        palette.TextReplaced += (_, text) => replaced = text;

        await palette.ExecuteAsync(ItemLabeled(palette, "タグを探す"));

        Assert.Equal("#", replaced);
        Assert.Equal("タグ", Assert.Single(palette.Rows.OfType<PaletteHeaderRow>()).Title);
    }

    [Fact]
    public async Task ExecuteAsync_ToggleTheme_KeepsPaletteOpenWithNotice()
    {
        _f.Host.ToggleTheme().Returns("ダーク");
        var palette = await OpenAsync(">テーマ");
        var closed = false;
        palette.CloseRequested += (_, _) => closed = true;

        await palette.ExecuteAsync(palette.Selected);

        Assert.False(closed);
        Assert.Equal("テーマを「ダーク」にしました", palette.FooterText);
    }

    [Fact]
    public async Task CreateFromTextAsync_ParsesInputAndShowsUndoToast()
    {
        NewTaskRequest? request = null;
        var created = new TaskItem { Title = "会議資料", DueAt = FixedClock.LocalToUtc(2026, 9, 23, 15, 0), DueHasTime = true };
        _f.Tasks.AddAsync(Arg.Do<NewTaskRequest>(r => request = r), Arg.Any<CancellationToken>()).Returns(PaletteFixture.Added(created));
        var palette = await OpenAsync("会議資料 明日 15:00 #仕事");
        var closed = false;
        palette.CloseRequested += (_, _) => closed = true;

        await palette.CreateFromTextAsync();

        Assert.NotNull(request);
        Assert.Equal("会議資料", request.Title);
        Assert.Equal(FixedClock.LocalToUtc(2026, 9, 23, 15, 0), request.DueAt);
        Assert.Equal(["仕事"], request.TagNames);
        Assert.Equal(["「会議資料」を追加しました（期限 明日 15:00）"], _f.Toasts);
        Assert.Equal(1, _f.Stack.Count);
        Assert.True(closed);
    }

    [Fact]
    public async Task CreateFromTextAsync_InsideProject_PutsTaskInThatProject()
    {
        var project = _f.AddProject("業務改善");
        NewTaskRequest? request = null;
        _f.Tasks.AddAsync(Arg.Do<NewTaskRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(PaletteFixture.Added(new TaskItem { Title = "議事録" }));
        var palette = await OpenAsync("@業務");
        await palette.NarrowAsync();
        await TypeAsync(palette, "議事録");

        await palette.CreateFromTextAsync();

        Assert.Equal(project.Id, request?.ProjectId);
    }

    [Fact]
    public async Task CreateFromTextAsync_WithSymbol_DoesNotCreate()
    {
        var palette = await OpenAsync(">テーマ");

        await palette.CreateFromTextAsync();

        await _f.Tasks.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task ToggleTaskAsync_OpenTask_CompletesKeepsOpenAndRecordsUndoWithoutToast()
    {
        var task = _f.AddTask("会議資料");
        _f.Tasks.SetCompletedAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(task.Id)), true, Arg.Any<CancellationToken>())
            .Returns(PaletteFixture.Changed(task));
        var palette = await OpenAsync("会議");
        var closed = false;
        palette.CloseRequested += (_, _) => closed = true;
        var item = Assert.IsType<PaletteTaskItem>(palette.Selected);

        await palette.ToggleTaskAsync(item);

        Assert.True(item.IsClosed);
        Assert.False(closed);
        Assert.Equal(1, _f.Stack.Count);
        Assert.Equal(UndoKind.Toggle, _f.Stack.Peek()?.Kind);
        Assert.Empty(_f.Toasts);
        Assert.Equal("「会議資料」を完了しました", palette.FooterText);
    }

    [Fact]
    public async Task ToggleTaskAsync_Recurring_ToastsNextDue()
    {
        var task = _f.AddTask("週報を出す", new DateTime(2026, 9, 22));
        var next = new TaskItem { Title = "週報を出す", DueAt = FixedClock.LocalToUtc(2026, 9, 29) };
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>()).Returns(PaletteFixture.Changed(task, next));
        var palette = await OpenAsync("週報");

        await palette.ToggleTaskAsync(null);

        Assert.Equal(["完了しました・次回は 9月29日（火）"], _f.Toasts);
    }

    [Fact]
    public async Task ToggleTaskAsync_ParentWithOpenSubtaskAndCancel_DoesNotWrite()
    {
        var parent = _f.AddTask("資料を作る");
        var child = new TaskItem { Title = "グラフ", ParentTaskId = parent.Id, Depth = 1 };
        _f.Tasks.GetDetailAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(new TaskDetail(parent, [], null, [child]));
        var palette = await OpenAsync("資料");
        palette.Confirm = _ => ConfirmChoice.Cancel;

        await palette.ToggleTaskAsync(null);

        await _f.Tasks.DidNotReceiveWithAnyArgs().SetCompletedAsync(default!, default);
        Assert.False(Assert.IsType<PaletteTaskItem>(palette.Selected).IsClosed);
    }

    [Fact]
    public async Task ToggleTaskAsync_ParentAndYes_CompletesSubtasksToo()
    {
        var parent = _f.AddTask("資料を作る");
        var child = new TaskItem { Title = "グラフ", ParentTaskId = parent.Id, Depth = 1 };
        _f.Tasks.GetDetailAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(new TaskDetail(parent, [], null, [child]));
        var palette = await OpenAsync("資料");
        palette.Confirm = _ => ConfirmChoice.Yes;

        await palette.ToggleTaskAsync(null);

        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(parent.Id) && ids.Contains(child.Id)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CarryOver_WritesAndShowsBulkToast()
    {
        var overdue = _f.AddTask("出し忘れ", new DateTime(2026, 9, 20));
        _f.Tasks.CarryOverOverdueAsync(Arg.Any<CancellationToken>()).Returns(PaletteFixture.Changed(overdue));
        var palette = await OpenAsync(">繰り越");

        await palette.ExecuteAsync(palette.Selected);

        await _f.Tasks.Received(1).CarryOverOverdueAsync(Arg.Any<CancellationToken>());
        Assert.Equal(["期限切れ 1 件を今日へ繰り越しました"], _f.Toasts);
        Assert.Equal(UndoKind.Bulk, _f.Stack.Peek()?.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_CarryOverWithNothingOverdue_StaysOpenWithNotice()
    {
        var palette = await OpenAsync(">繰り越");
        var closed = false;
        palette.CloseRequested += (_, _) => closed = true;

        await palette.ExecuteAsync(palette.Selected);

        Assert.False(closed);
        Assert.Equal("期限切れのタスクはありません", palette.FooterText);
        Assert.Empty(_f.Toasts);
    }

    [Fact]
    public async Task ExecuteAsync_Template_ExpandsFromTodayAndShowsToast()
    {
        var template = _f.AddTemplate("週次レビュー", ("振り返る", 0), ("来週の予定", 1));
        ExpansionPlan? plan = null;
        _f.Templates.ExpandAsync(Arg.Do<ExpansionPlan>(p => plan = p), Arg.Any<CancellationToken>())
            .Returns(PaletteFixture.Added(new TaskItem { Title = "振り返る" }, new TaskItem { Title = "来週の予定" }));
        var palette = await OpenAsync("+週次");

        await palette.ExecuteAsync(palette.Selected);

        Assert.NotNull(plan);
        Assert.Equal(template.Template.Id, plan.TemplateId);
        Assert.Equal(new DateOnly(2026, 9, 22), plan.AnchorDate);
        Assert.Equal(new DateTime?[] { FixedClock.LocalToUtc(2026, 9, 22), FixedClock.LocalToUtc(2026, 9, 23) }, plan.Tasks.Select(t => t.DueAt));
        Assert.Equal(["「週次レビュー」から 2 件を作りました"], _f.Toasts);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteCompleted_SoftDeletesClosedTasks()
    {
        var done = _f.AddTask("済んだ", status: TaskItemStatus.Completed);
        _f.AddTask("まだ");
        _f.Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(PaletteFixture.Changed(done));
        var palette = await OpenAsync(">一括削除");

        await palette.ExecuteAsync(palette.Selected);

        await _f.Tasks.Received(1).SoftDeleteAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(done.Id)), Arg.Any<CancellationToken>());
        Assert.Equal(["完了済みの 1 件をゴミ箱に移しました"], _f.Toasts);
    }
}
