using TaskDeck.App.Controls;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;

namespace TaskDeck.Tests.ImportExport;

public sealed class FilterPanelModelTests
{
    private static readonly Project Work = new() { Name = "業務改善", ColorHex = "#0067C0" };
    private static readonly Project Archived = new() { Name = "旧案件", ColorHex = "#CA5010", IsArchived = true };
    private static readonly Tag Job = new() { Name = "仕事" };
    private static readonly Tag Meeting = new() { Name = "会議" };

    /// <summary>絞り込み以外の項目をすべて既定でない値にした条件（これらは変わってはいけない）。</summary>
    private static readonly TaskQuery Base = new()
    {
        Status = TaskStatusFilter.All,
        SearchText = "資料",
        ClosedFrom = new DateOnly(2026, 9, 1),
        TemplateBatchId = Guid.CreateVersion7(),
        SortKey = TaskSortKey.Due,
        Descending = true,
        IncludeSubtasks = false,
    };

    [Fact]
    public void Apply_SelectionsChanged_SetsOnlyFilterFields()
    {
        var model = NewModel(Base);
        Pick(model.Statuses, TaskItemStatus.InProgress, TaskItemStatus.Completed);
        PickOne(model.Priorities, Priority.High);
        PickOne(model.DueOptions, DueFilter.Overdue);
        PickOne(model.Projects, model.Projects.Single(o => o.Value.Id == Work.Id).Value);
        Pick(model.Tags, Meeting.Id);

        var applied = model.Apply(Base);

        Assert.Equal([TaskItemStatus.InProgress, TaskItemStatus.Completed], applied.Statuses);
        Assert.Equal(Priority.High, applied.MinPriority);
        Assert.Equal(DueFilter.Overdue, applied.Due);
        Assert.Null(applied.DueFrom);
        Assert.Null(applied.DueTo);
        Assert.Equal(Work.Id, applied.ProjectId);
        Assert.False(applied.WithoutProject);
        Assert.Equal([Meeting.Id], applied.TagIds);
        AssertNonFilterFieldsKept(applied);
    }

    [Fact]
    public void Apply_LoadedWithoutChanges_ReturnsEqualQuery()
    {
        var query = Base with
        {
            Statuses = [TaskItemStatus.NotStarted],
            MinPriority = Priority.Medium,
            Due = DueFilter.Range,
            DueFrom = new DateOnly(2026, 9, 1),
            DueTo = new DateOnly(2026, 9, 30),
            WithoutProject = true,
            TagIds = [Job.Id, Meeting.Id],
        };

        var model = NewModel(query);

        Assert.Equal(query, model.Apply(query));
        Assert.Equal(ProjectChoiceKind.None, model.SelectedProject.Kind);
        Assert.True(model.RangeOption.IsSelected);
    }

    [Fact]
    public void Clear_ThenApply_ResetsOnlyFilterFields()
    {
        var query = Base with
        {
            Statuses = [TaskItemStatus.Cancelled],
            MinPriority = Priority.Urgent,
            Due = DueFilter.Range,
            DueFrom = new DateOnly(2026, 9, 1),
            ProjectId = Work.Id,
            TagIds = [Job.Id],
        };
        var model = NewModel(query);

        model.Clear();
        var applied = model.Apply(query);

        Assert.Empty(applied.Statuses);
        Assert.Null(applied.MinPriority);
        Assert.Equal(DueFilter.Any, applied.Due);
        Assert.Null(applied.DueFrom);
        Assert.Null(applied.DueTo);
        Assert.Null(applied.ProjectId);
        Assert.False(applied.WithoutProject);
        Assert.Empty(applied.TagIds);
        AssertNonFilterFieldsKept(applied);
    }

    [Fact]
    public void Apply_RangeWithReversedDates_SwapsThem()
    {
        var model = NewModel(Base);
        PickOne(model.DueOptions, DueFilter.Range);
        model.RangeStart = new DateTime(2026, 9, 30);
        model.RangeEnd = new DateTime(2026, 9, 10);

        var applied = model.Apply(Base);

        Assert.Equal(new DateOnly(2026, 9, 10), applied.DueFrom);
        Assert.Equal(new DateOnly(2026, 9, 30), applied.DueTo);
    }

    [Fact]
    public void Apply_DueOtherThanRange_DropsDates()
    {
        var query = Base with { Due = DueFilter.Range, DueFrom = new DateOnly(2026, 9, 1), DueTo = new DateOnly(2026, 9, 2) };
        var model = NewModel(query);

        PickOne(model.DueOptions, DueFilter.Next7Days);
        var applied = model.Apply(query);

        Assert.Equal(DueFilter.Next7Days, applied.Due);
        Assert.Null(applied.DueFrom);
        Assert.Null(applied.DueTo);
    }

    [Fact]
    public void Load_UnlistedDue_IsKeptUntilDueChanged()
    {
        var query = Base with { Due = DueFilter.NoDueDate };
        var model = NewModel(query);

        Assert.DoesNotContain(model.DueOptions, o => o.IsSelected);
        Assert.Equal(DueFilter.NoDueDate, model.Apply(query).Due);

        PickOne(model.DueOptions, DueFilter.TodayOrOverdue);
        Assert.Equal(DueFilter.TodayOrOverdue, model.Apply(query).Due);
    }

    [Fact]
    public void Load_DeletedProjectOrTag_IsDroppedOnApply()
    {
        var query = Base with { ProjectId = Guid.CreateVersion7(), TagIds = [Job.Id, Guid.CreateVersion7()] };
        var model = NewModel(query);

        var applied = model.Apply(query);

        Assert.Equal(ProjectChoiceKind.Any, model.SelectedProject.Kind);
        Assert.Single(model.Projects, o => o.IsSelected);
        Assert.Null(applied.ProjectId);
        Assert.Equal([Job.Id], applied.TagIds);
    }

    [Fact]
    public void SetChoices_Projects_OffersAnyNoneAndArchivedWithMark()
    {
        var model = NewModel(Base);

        Assert.Equal(
            ["指定しない", "プロジェクトなし", "業務改善", "旧案件（アーカイブ）"],
            model.Projects.Select(p => p.Label));
        Assert.Equal([null, null, "#0067C0", "#CA5010"], model.Projects.Select(p => p.Value.ColorHex));
        Assert.Equal(["#仕事", "#会議"], model.Tags.Select(t => t.Label));
        Assert.True(model.Projects[0].IsSelected);   // 何も絞っていなければ「指定しない」
    }

    [Fact]
    public void CreateSavedFilter_Name_TrimsAndDropsSearchText()
    {
        var model = NewModel(Base);
        Pick(model.Tags, Job.Id);

        var result = model.CreateSavedFilter("  仕事の残り  ", Base);

        Assert.True(result.Succeeded);
        var filter = result.Value!;
        Assert.Equal("仕事の残り", filter.Name);
        Assert.Null(filter.Query.SearchText);
        Assert.Equal([Job.Id], filter.Query.TagIds);
        Assert.Equal(Base.SortKey, filter.Query.SortKey);
        Assert.Equal(Base.Status, filter.Query.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("　 ")]
    public void CreateSavedFilter_BlankName_Fails(string? name)
    {
        var result = NewModel(Base).CreateSavedFilter(name, Base);

        Assert.False(result.Succeeded);
        Assert.Equal("名前を入れてください。", result.Error);
    }

    private static FilterPanelModel NewModel(TaskQuery query)
    {
        var model = new FilterPanelModel();
        model.SetChoices([Work, Archived], [Job, Meeting], dark: false);
        model.Load(query);
        return model;
    }

    private static void Pick<T>(IEnumerable<FilterOption<T>> options, params T[] values)
    {
        foreach (var option in options)
        {
            option.IsSelected = values.Contains(option.Value);
        }
    }

    /// <summary>画面の RadioButton と同じく、1つだけ選ぶ。</summary>
    private static void PickOne<T>(IEnumerable<FilterOption<T>> options, T value) => Pick(options, value);

    private static void AssertNonFilterFieldsKept(TaskQuery applied)
    {
        Assert.Equal(Base.Status, applied.Status);
        Assert.Equal(Base.SearchText, applied.SearchText);
        Assert.Equal(Base.ClosedFrom, applied.ClosedFrom);
        Assert.Equal(Base.TemplateBatchId, applied.TemplateBatchId);
        Assert.Equal(Base.SortKey, applied.SortKey);
        Assert.Equal(Base.Descending, applied.Descending);
        Assert.Equal(Base.IncludeSubtasks, applied.IncludeSubtasks);
    }
}
