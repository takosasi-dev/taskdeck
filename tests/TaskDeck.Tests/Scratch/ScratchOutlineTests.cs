using TaskDeck.Core.Scratch;

namespace TaskDeck.Tests.Scratch;

/// <summary>字下げの規則（3段まで・前の行より2段以上深くしない・子ごと動かす）。</summary>
public class ScratchOutlineTests
{
    /// <summary>"A0 B1 C2" の形で行を作る（末尾の数字が深さ）。</summary>
    private static List<ScratchItem> Items(string shape) =>
        [.. shape.Split(' ').Select(s => new ScratchItem { Text = s[..^1], Depth = s[^1] - '0' })];

    private static string Shape(List<ScratchItem> items) => string.Join(' ', items.Select(i => i.Text + i.Depth));

    [Theory]
    [InlineData("A0 B0", 1, "A0 B1")]
    [InlineData("A0 B0 C1", 1, "A0 B1 C2")]   // 子ごと下がる
    [InlineData("A0 B1 C1", 2, "A0 B1 C2")]
    public void Indent_下げられる_子ごと1段下がる(string before, int index, string after)
    {
        var items = Items(before);

        Assert.True(ScratchOutline.Indent(items, index));
        Assert.Equal(after, Shape(items));
    }

    [Theory]
    [InlineData("A0", 0)]             // 先頭の行
    [InlineData("A0 B1", 1)]          // 前の行より2段深くなる
    [InlineData("A0 B1 C2 D2", 3)]    // 3段を超える
    [InlineData("A0 B0 C1 D2", 1)]    // 子（孫）が3段を超える
    public void Indent_下げられない_何も変えずfalse(string before, int index)
    {
        var items = Items(before);

        Assert.False(ScratchOutline.Indent(items, index));
        Assert.Equal(before, Shape(items));
    }

    [Theory]
    [InlineData("A0 B1 C2", 1, "A0 B0 C1")]   // 子ごと上がる
    [InlineData("A0 B1 C1", 1, "A0 B0 C1")]   // 並びは変えない（C は B の子になる）
    [InlineData("A0 B1 C2", 2, "A0 B1 C1")]
    public void Outdent_上げられる_子ごと1段上がる(string before, int index, string after)
    {
        var items = Items(before);

        Assert.True(ScratchOutline.Outdent(items, index));
        Assert.Equal(after, Shape(items));
    }

    [Fact]
    public void Outdent_いちばん上の段_何もしない()
    {
        var items = Items("A0 B1");

        Assert.False(ScratchOutline.Outdent(items, 0));
        Assert.Equal("A0 B1", Shape(items));
    }

    [Theory]
    [InlineData("A0 B0", 0, 1, 0)]       // 子が無ければ同じ深さで下に
    [InlineData("A0 B1", 0, 1, 1)]       // 子があれば最初の子に
    [InlineData("A0 B1 C2", 2, 3, 2)]    // 末尾
    public void InsertBelow_すぐ下に空の行を足す(string before, int index, int newIndex, int depth)
    {
        var items = Items(before);

        var added = ScratchOutline.InsertBelow(items, index);

        Assert.Same(added, items[newIndex]);
        Assert.Equal(depth, added.Depth);
        Assert.Equal("", added.Text);
    }

    [Theory]
    [InlineData("A0 B1 C2 D1", 1, "A0 C1 D1")]   // 子は1段上がって消した行の場所に入る
    [InlineData("A0 B1", 0, "B0")]
    [InlineData("A0 B0 C0", 1, "A0 C0")]
    public void Remove_消した行の子は1段上がる(string before, int index, string after)
    {
        var items = Items(before);

        ScratchOutline.Remove(items, index);

        Assert.Equal(after, Shape(items));
    }

    [Fact]
    public void Normalize_深すぎる行と宙に浮いた行_詰める()
    {
        var items = Items("A2 B5 C0 D2");

        ScratchOutline.Normalize(items);

        Assert.Equal("A0 B1 C0 D1", Shape(items));
    }
}
