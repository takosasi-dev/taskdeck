using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>テンプレートから一度に作った一群（F-15B）のグループ分けと、見出しからのまとめて完了・削除。</summary>
public partial class TaskListViewModelTests
{
    private TaskItem BatchTask(string title, Guid batchId, Guid? parentId = null)
    {
        var task = _f.NewTask(title, parentId: parentId);
        task.TemplateBatchId = batchId;
        task.CreatedAt = FixedClock.LocalToUtc(2026, 9, 21, 9);
        return task;
    }

    private void GivenWeeklyReviewTemplate() =>
        _f.TemplateList.Add(new TemplateWithItems(
            new TaskTemplate { Name = "週次レビュー" },
            [new TaskTemplateItem { Title = "受信箱を空にする" }, new TaskTemplateItem { Title = "来週の予定を確認" }]));

    private static IEnumerable<string> Outline(TaskListViewModel list) => list.Rows.Select(item => item switch
    {
        TaskRowViewModel row => row.Title,
        TemplateBatchHeaderViewModel header => "[" + header.Title + "]",
        AddTaskRowViewModel => "+",
        _ => "?",
    });

    [Fact]
    public async Task ShowAsync_同じ一群が2件以上_最初の位置に見出しを付けてまとめる()
    {
        var batch = Guid.CreateVersion7();
        _f.AddRow(_f.NewTask("X"));
        _f.AddRow(BatchTask("受信箱を空にする", batch));
        _f.AddRow(_f.NewTask("Y"));
        _f.AddRow(BatchTask("来週の予定を確認", batch));
        GivenWeeklyReviewTemplate();

        var list = await ShowAllAsync();

        Assert.Equal(["X", "[週次レビュー（9/21 に作成）]", "受信箱を空にする", "来週の予定を確認", "Y", "+"], Outline(list));
        Assert.Equal(2, list.Rows.OfType<TemplateBatchHeaderViewModel>().Single().Count);
    }

    [Fact]
    public async Task ShowAsync_一群が親と子の1ブロックでも2件以上_見出しを付けて子も数える()
    {
        var batch = Guid.CreateVersion7();
        var parent = BatchTask("買い物リスト", batch);
        _f.AddRow(parent);
        _f.AddRow(BatchTask("牛乳", batch, parent.Id), isContext: true);

        var list = await ShowAllAsync();

        Assert.Equal(["[テンプレート（9/21 に作成）]", "買い物リスト", "牛乳", "+"], Outline(list));
    }

    [Fact]
    public async Task ShowAsync_一群が1件だけ_見出しを付けない()
    {
        _f.AddRow(BatchTask("受信箱を空にする", Guid.CreateVersion7()));
        _f.AddRow(_f.NewTask("X"));

        var list = await ShowAllAsync();

        Assert.Empty(list.Rows.OfType<TemplateBatchHeaderViewModel>());
    }

    [Fact]
    public async Task SetSearchAsync_検索中_一群でもまとめない()
    {
        var batch = Guid.CreateVersion7();
        _f.AddRow(BatchTask("受信箱を空にする", batch));
        _f.AddRow(BatchTask("来週の予定を確認", batch));
        var list = await ShowAllAsync();

        await list.SetSearchAsync("来週");

        Assert.Empty(list.Rows.OfType<TemplateBatchHeaderViewModel>());
    }

    [Fact]
    public async Task ShowAsync_テンプレートを読むのは一群があるときだけ_一度当てた名前は覚えておく()
    {
        var list = await ShowAllAsync();
        await _f.Templates.DidNotReceiveWithAnyArgs().GetAllAsync(default);

        var batch = Guid.CreateVersion7();
        _f.AddRow(BatchTask("受信箱を空にする", batch));
        _f.AddRow(BatchTask("来週の予定を確認", batch));
        GivenWeeklyReviewTemplate();
        await list.ReloadAsync();
        await list.ReloadAsync();

        await _f.Templates.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        Assert.Equal("週次レビュー", list.Rows.OfType<TemplateBatchHeaderViewModel>().Single().Name);
    }

    [Fact]
    public async Task CompleteBatchAsync_見出しの下の一群_未完了をまとめて完了にして取り消し1段()
    {
        var batch = Guid.CreateVersion7();
        var a = BatchTask("受信箱を空にする", batch);
        var b = BatchTask("来週の予定を確認", batch);
        _f.AddRow(_f.NewTask("X"));
        _f.AddRow(a);
        _f.AddRow(b);
        GivenWeeklyReviewTemplate();
        var list = await ShowAllAsync();
        _f.Tasks.SetCompletedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>()).Returns(ViewModelFixture.Changed(a));

        await list.CompleteBatchAsync(list.Rows.OfType<TemplateBatchHeaderViewModel>().Single());

        await _f.Tasks.Received(1).SetCompletedAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, b.Id })), true, Arg.Any<CancellationToken>());
        Assert.Equal(["「週次レビュー」の 2 件を完了にしました"], _f.Toasts);
        Assert.Equal(UndoKind.Bulk, _f.Stack.Peek()!.Kind);
    }

    [Fact]
    public async Task DeleteBatchAsync_見出しの下の一群_まとめてゴミ箱へ移してトースト()
    {
        var batch = Guid.CreateVersion7();
        var a = BatchTask("受信箱を空にする", batch);
        var b = BatchTask("来週の予定を確認", batch);
        _f.AddRow(a);
        _f.AddRow(b);
        _f.AddRow(_f.NewTask("X"));
        GivenWeeklyReviewTemplate();
        var list = await ShowAllAsync();
        _f.Tasks.SoftDeleteAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(ViewModelFixture.Changed(a));

        await list.DeleteBatchAsync(list.Rows.OfType<TemplateBatchHeaderViewModel>().Single());

        await _f.Tasks.Received(1).SoftDeleteAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { a.Id, b.Id })), Arg.Any<CancellationToken>());
        Assert.Equal(["「週次レビュー」の 2 件を削除しました"], _f.Toasts);
    }

    [Fact]
    public void MatchTemplate_一致数が同じ_最近使った方にし一致が無ければnull()
    {
        var old = new TaskListViewModel.TemplateTitles("古い", FixedClock.LocalToUtc(2026, 1, 1), new HashSet<string> { "a", "b" });
        var recent = new TaskListViewModel.TemplateTitles("最近", FixedClock.LocalToUtc(2026, 9, 1), new HashSet<string> { "a", "c" });

        Assert.Equal("最近", TaskListViewModel.MatchTemplate(["A", "x"], [old, recent]));
        Assert.Equal("古い", TaskListViewModel.MatchTemplate(["a", "b"], [old, recent]));
        Assert.Null(TaskListViewModel.MatchTemplate(["z"], [old, recent]));
    }
}
