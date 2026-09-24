using System.Collections.Specialized;
using TaskDeck.App.ViewModels;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>一覧の行の差し替え（少ないときは位置ごと、多いときは Reset 1回）。</summary>
public class RowCollectionTests
{
    private static TaskGroupHeaderViewModel H(string title) => new(title, 1);

    [Fact]
    public void Update_入力行が後ろへずれる_同じ物を2つ並べずに新しい並びにする()
    {
        var add = new AddTaskRowViewModel();
        var a = H("A");
        var rows = new RowCollection { a, add };
        var b = H("B");
        var seen = new List<IReadOnlyList<ListItemViewModel>>();
        rows.CollectionChanged += (_, _) => seen.Add([.. rows]);

        rows.Update([a, b, add]);

        Assert.Equal([a, b, add], rows);
        Assert.All(seen, snapshot => Assert.Equal(snapshot.Count, snapshot.Distinct().Count()));
        Assert.DoesNotContain(seen, snapshot => snapshot.Count == 0);
    }

    [Fact]
    public void Update_少ない件数_Resetを使わず位置ごとに差し替える()
    {
        var rows = new RowCollection { H("A"), H("B"), H("C") };
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        var next = new ListItemViewModel[] { H("D"), H("E") };

        rows.Update(next);

        Assert.Equal(next, rows);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void Update_上限を超える件数_Reset1回で入れ替える()
    {
        var rows = new RowCollection { H("A") };
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        var many = Enumerable.Range(0, RowCollection.IncrementalLimit + 1).Select(i => (ListItemViewModel)H(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();

        rows.Update(many);

        Assert.Equal(many.Count, rows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
    }
}
