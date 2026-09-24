using NSubstitute;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>サイドバー（ビュー・プロジェクト・タグ・保存済みフィルタと件数、名前の入力、消したビューからの移動）。</summary>
public class SidebarViewModelTests
{
    private readonly ViewModelFixture _f = new();

    [Fact]
    public async Task ReloadAsync_件数つきでビューとプロジェクトとタグと保存済みフィルタを並べる()
    {
        var project = new Project { Name = "業務改善", ColorHex = ProjectPalette.Light[0] };
        var tag = new Tag { Name = "仕事" };
        _f.ProjectList.Add(project);
        _f.TagList.Add(tag);
        _f.Settings.Current.SavedFilters.Add(new SavedFilter { Name = "急ぎの仕事" });
        _f.Counts = new ViewCounts(12, 2, 28, 94, 3, new Dictionary<Guid, int> { [project.Id] = 5 }, new Dictionary<Guid, int> { [tag.Id] = 8 });
        var sidebar = _f.CreateSidebar();

        await sidebar.ReloadAsync();

        var rows = sidebar.Items.Where(i => i.IsRow).ToList();
        Assert.Equal(["今日", "予定", "すべて", "業務改善", "プロジェクトを追加", "仕事", "急ぎの仕事"], rows.Select(i => i.Label));
        Assert.Equal([12, 28, 94, 5], rows.Take(4).Select(i => i.Count));
        Assert.Equal(8, rows.Single(i => i.IsTag).Count);
        Assert.Equal(6, rows.Single(i => i.IsProject).ColorChoices.Count);
        Assert.Equal(["完了済み", "ゴミ箱", "振り返り", "テンプレート"], sidebar.BottomItems.Select(i => i.Label));
        Assert.Equal(3, sidebar.BottomItems.Single(i => i.Key == ViewKey.Trash).Count);
        Assert.Equal("#仕事", sidebar.LabelOf(ViewKey.ForTag(tag.Id)));
    }

    [Fact]
    public async Task SetCurrentView_選んだ項目だけ選択中にする()
    {
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();

        sidebar.SetCurrentView(ViewKey.Upcoming);

        Assert.Equal(["予定"], sidebar.Items.Concat(sidebar.BottomItems).Where(i => i.IsSelected).Select(i => i.Label));
    }

    [Fact]
    public async Task CommitEditAsync_プロジェクトを追加_作ってそのビューを開く()
    {
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        var created = new Project { Name = "学校" };
        _f.Projects.AddAsync("学校", null, Arg.Any<CancellationToken>()).Returns(created);
        var opened = new List<ViewKey>();
        sidebar.ViewSelected += (_, key) => opened.Add(key);
        var add = sidebar.Items.Single(i => i.IsAddProject);

        add.InvokeCommand!.Execute(add);
        add.EditText = "  学校 ";
        await sidebar.CommitEditAsync(add);
        await sidebar.CommitEditAsync(add);

        await _f.Projects.Received(1).AddAsync("学校", null, Arg.Any<CancellationToken>());
        Assert.Equal([ViewKey.ForProject(created.Id)], opened);
    }

    [Fact]
    public async Task CommitEditAsync_空のまま確定_何も作らない()
    {
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        var add = sidebar.Items.Single(i => i.IsAddProject);

        add.InvokeCommand!.Execute(add);
        await sidebar.CommitEditAsync(add);

        await _f.Projects.DidNotReceiveWithAnyArgs().AddAsync(default!, default, default);
        Assert.False(add.IsEditing);
    }

    [Fact]
    public async Task CommitEditAsync_タグ名が重なる_知らせを出す()
    {
        var tag = new Tag { Name = "仕事" };
        _f.TagList.Add(tag);
        _f.Tags.RenameAsync(tag.Id, "会議", Arg.Any<CancellationToken>()).Returns(OperationResult.Fail("重複"));
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        var notices = new List<string>();
        sidebar.NoticeRequested += (_, m) => notices.Add(m);
        var item = sidebar.Items.Single(i => i.IsTag);

        item.BeginRenameCommand!.Execute(item);
        item.EditText = "会議";
        await sidebar.CommitEditAsync(item);

        Assert.Single(notices);
    }

    [Fact]
    public async Task DeleteProject_いま開いているプロジェクト_今日へ移る()
    {
        var project = new Project { Name = "業務改善" };
        _f.ProjectList.Add(project);
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        sidebar.SetCurrentView(ViewKey.ForProject(project.Id));
        var opened = new List<ViewKey>();
        sidebar.ViewSelected += (_, key) => opened.Add(key);
        var item = sidebar.Items.Single(i => i.IsProject);

        _f.ProjectList.Clear();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.DeleteCommand!).ExecuteAsync(item);

        await _f.Projects.Received(1).DeleteAsync(project.Id, Arg.Any<CancellationToken>());
        Assert.Equal([ViewKey.Today], opened);
    }

    [Fact]
    public async Task Contains_消えたプロジェクト_無いと答える()
    {
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();

        Assert.False(sidebar.Contains(ViewKey.ForProject(Guid.CreateVersion7())));
        Assert.True(sidebar.Contains(ViewKey.Completed));
    }

    [Fact]
    public async Task RefreshCountsAsync_項目を作り直さず件数だけ変える()
    {
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        var today = sidebar.Items[0];

        _f.Counts = ViewCounts.Empty with { Today = 4 };
        await sidebar.RefreshCountsAsync();

        Assert.Same(today, sidebar.Items[0]);
        Assert.Equal(4, today.Count);
    }

    // ---- タグの管理（F-045） ----

    [Fact]
    public async Task DeleteUnusedTagsCommand_開いているタグが消えた_件数を知らせて今日へ移る()
    {
        var tag = new Tag { Name = "使っていない" };
        _f.TagList.Add(tag);
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        sidebar.SetCurrentView(ViewKey.ForTag(tag.Id));
        _f.Tags.DeleteUnusedAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _f.TagList.Clear();
            return 1;
        });
        var notices = new List<string>();
        sidebar.NoticeRequested += (_, m) => notices.Add(m);
        var opened = new List<ViewKey>();
        sidebar.ViewSelected += (_, key) => opened.Add(key);

        await sidebar.DeleteUnusedTagsCommand.ExecuteAsync(null);

        Assert.Equal(["使っていないタグを 1 件削除しました"], notices);
        Assert.DoesNotContain(sidebar.Items, i => i.IsTag);
        Assert.Equal([ViewKey.Today], opened);
    }

    [Fact]
    public async Task DeleteUnusedTagsCommand_消すものが無い_無かったと知らせる()
    {
        var sidebar = _f.CreateSidebar();
        await sidebar.ReloadAsync();
        var notices = new List<string>();
        sidebar.NoticeRequested += (_, m) => notices.Add(m);

        await sidebar.DeleteUnusedTagsCommand.ExecuteAsync(null);

        Assert.Equal(["使っていないタグはありませんでした"], notices);
    }
}
