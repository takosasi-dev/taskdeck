using TaskDeck.App.Controls;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>絞り込みパネルの「クリア」（BaseQuery＝ビューの条件へ戻す。INTERFACES 5.8）。</summary>
public class FilterPanelBaseQueryTests
{
    [Fact]
    public void ResetTo_プロジェクトのビュー_優先度を外してそのプロジェクトに戻す()
    {
        var project = new Project { Name = "業務改善" };
        var other = new Project { Name = "学校" };
        var model = new FilterPanelModel();
        model.SetChoices([project, other], [], dark: false);
        var viewQuery = BuiltInViews.QueryFor(ViewKey.ForProject(project.Id));
        model.Load(viewQuery with { ProjectId = other.Id, MinPriority = Priority.High });

        model.ResetTo(viewQuery);
        var applied = model.Apply(viewQuery);

        Assert.Equal(project.Id, model.SelectedProject.Id);
        Assert.Equal(project.Id, applied.ProjectId);
        Assert.Null(applied.MinPriority);
    }

    [Fact]
    public void ResetTo_今日ビュー_期限は今日までに戻す()
    {
        var model = new FilterPanelModel();
        var viewQuery = BuiltInViews.QueryFor(ViewKey.Today);
        model.Load(viewQuery with { Due = DueFilter.Next7Days, Statuses = [TaskItemStatus.InProgress] });

        model.ResetTo(viewQuery);
        var applied = model.Apply(viewQuery);

        Assert.Equal(DueFilter.TodayOrOverdue, applied.Due);
        Assert.Empty(applied.Statuses);
    }

    [Fact]
    public void ResetTo_ビューの条件が無い_すべて指定なしに戻す()
    {
        var model = new FilterPanelModel();
        model.Load(new TaskQuery { Due = DueFilter.Overdue, MinPriority = Priority.Low });

        model.ResetTo(null);
        var applied = model.Apply(new TaskQuery());

        Assert.Equal(DueFilter.Any, applied.Due);
        Assert.Null(applied.MinPriority);
    }
}
