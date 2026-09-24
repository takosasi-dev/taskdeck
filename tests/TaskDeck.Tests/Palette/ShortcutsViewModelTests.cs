using TaskDeck.App.Views.Shortcuts;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Palette;

public class ShortcutsViewModelTests
{
    private static ShortcutsViewModel Create(Action<FakeSettingsStore>? arrange = null)
    {
        var settings = new FakeSettingsStore();
        arrange?.Invoke(settings);
        return new ShortcutsViewModel(settings);
    }

    private static IEnumerable<ShortcutRow> AllRows(ShortcutsViewModel viewModel) =>
        viewModel.LeftColumn.Concat(viewModel.RightColumn).SelectMany(c => c.Rows);

    private static IEnumerable<string> VisibleActions(ShortcutsViewModel viewModel) =>
        AllRows(viewModel).Where(r => r.IsVisible).Select(r => r.Action);

    [Fact]
    public void Constructor_GlobalHotkeys_ComeFromSettingsWithPencil()
    {
        var viewModel = Create(s => s.Current.Hotkeys.QuickInput = "Ctrl+Alt+Q");

        var global = viewModel.LeftColumn[0];
        Assert.Equal("どのアプリからでも", global.Title);
        Assert.True(global.IsAccent);
        Assert.All(global.Rows, r => Assert.True(r.IsEditable));
        Assert.Equal(["Ctrl", "+", "Alt", "+", "Q"], global.Rows[0].Keys.Select(k => k.Text));
        Assert.Equal("Ctrl+Shift+F", global.Rows[2].KeysText);
    }

    [Fact]
    public void Constructor_EmptyHotkey_ShowsUnset()
    {
        var viewModel = Create(s => s.Current.Hotkeys.FocusMode = "");

        Assert.Equal("未設定", viewModel.LeftColumn[0].Rows[2].KeysText);
    }

    [Fact]
    public void Constructor_AppKeys_OnlyThoseTheMainWindowHandles()
    {
        var keys = AllRows(Create()).Where(r => !r.IsEditable).Select(r => r.KeysText).ToList();

        Assert.Contains("Ctrl+K", keys);
        Assert.Contains("Ctrl+1〜6", keys);
        Assert.DoesNotContain("Ctrl+S", keys);   // 同期は Phase 5
        Assert.DoesNotContain("Ctrl+;", keys);   // メイン画面に無い
        Assert.DoesNotContain("Ctrl+R", keys);
    }

    [Fact]
    public void Constructor_PaletteAndHelp_AreAccent()
    {
        var accent = AllRows(Create()).Where(r => r.IsAccent).Select(r => r.KeysText);

        Assert.Equal(["Ctrl+K", "?"], accent);
    }

    [Fact]
    public void Constructor_ScratchList_HasItsOwnSectionWithNote()
    {
        var scratch = Create().RightColumn.Single(c => c.Title == "使い捨てリストの中で");

        Assert.Equal(
            ["Enter", "Ctrl+Enter", "Tab または Shift+Tab", "Backspace", "↑ または ↓", "Ctrl+Tab", "Ctrl+N"],
            scratch.Rows.Select(r => r.KeysText));
        Assert.True(scratch.HasNote);
        Assert.Contains("Ctrl+Z", scratch.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyFilter_Scratch_FindsScratchRowsByTheWord()
    {
        var viewModel = Create();

        viewModel.ApplyFilter("使い捨て");

        Assert.Equal(["使い捨てリストの中で"], viewModel.LeftColumn.Concat(viewModel.RightColumn).Where(c => c.IsVisible).Select(c => c.Title));
        Assert.Equal(7, VisibleActions(viewModel).Count());
    }

    [Fact]
    public void ApplyFilter_ActionWord_ShowsOnlyMatchingRowsAndCategories()
    {
        var viewModel = Create();

        viewModel.ApplyFilter("削除");

        Assert.Equal(["ゴミ箱に入れる", "空の行を消す（子は1段上がる）"], VisibleActions(viewModel));
        Assert.Equal(["タスクの操作", "使い捨てリストの中で"], viewModel.LeftColumn.Concat(viewModel.RightColumn).Where(c => c.IsVisible).Select(c => c.Title));
        Assert.True(viewModel.HasResults);
    }

    [Fact]
    public void ApplyFilter_KatakanaAndHiragana_MatchEachOther()
    {
        var viewModel = Create();

        viewModel.ApplyFilter("こまんどぱれっと");

        Assert.Equal(["コマンドパレット"], VisibleActions(viewModel));
    }

    [Fact]
    public void ApplyFilter_KeyName_DoesNotMatch()
    {
        var viewModel = Create();

        viewModel.ApplyFilter("ctrl");

        Assert.Empty(VisibleActions(viewModel));
        Assert.False(viewModel.HasResults);
    }

    [Fact]
    public void ApplyFilter_Cleared_ShowsEverythingAgain()
    {
        var viewModel = Create();
        viewModel.ApplyFilter("複製");

        viewModel.ApplyFilter("");

        Assert.All(AllRows(viewModel), r => Assert.True(r.IsVisible));
        Assert.True(viewModel.HasResults);
    }

    [Fact]
    public void ParseKeys_ComboAndAlternatives_SplitsIntoCapsPlusAndOr()
    {
        var tokens = ShortcutsViewModel.ParseKeys("Tab|Shift+Tab");

        Assert.Equal(
            [("Tab", KeyTokenKind.Cap), ("／", KeyTokenKind.Or), ("Shift", KeyTokenKind.Cap), ("+", KeyTokenKind.Plus), ("Tab", KeyTokenKind.Cap)],
            tokens.Select(t => (t.Text, t.Kind)));
    }

    [Fact]
    public void ParseKeys_Range_StaysOneCap()
    {
        var tokens = ShortcutsViewModel.ParseKeys("Ctrl+1〜6");

        Assert.Equal(["Ctrl", "+", "1〜6"], tokens.Select(t => t.Text));
    }
}
