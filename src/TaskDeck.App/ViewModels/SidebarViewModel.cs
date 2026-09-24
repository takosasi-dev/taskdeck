using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Scratch;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.ViewModels;

public enum NavItemKind
{
    View,
    Project,
    Tag,
    SavedFilter,
    Section,
    Separator,
    AddProject,
    /// <summary>ビューではなく別画面を開く項目（テンプレートなど）。</summary>
    Command,
    /// <summary>使い捨てリスト（タスクの一覧ではない。件数は出さない）。</summary>
    Scratch,
}

/// <summary>プロジェクトの色変更メニューの1項目（ContextMenu から直接呼べるようコマンドも持つ）。</summary>
public sealed record ColorChoice(Guid ProjectId, string Hex, string Name, IRelayCommand? Command = null);

/// <summary>サイドバーのプロジェクト・タグにタスクを落とした（プロジェクトへ移す・タグを付ける。MainViewModel が一覧に頼む）。</summary>
public sealed record SidebarDrop(NavItemViewModel Target, IReadOnlyList<Guid> TaskIds);

/// <summary>サイドバーの1行（UI 設計書 6.5）。見出し・区切りも同じ型で並べる。</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    public required NavItemKind Kind { get; init; }

    public ViewKey? Key { get; init; }

    public Guid? EntityId { get; init; }

    /// <summary>別画面を開く項目の識別子（templates）。見出しに付けた「＋」もこれで知らせる（newScratch）。</summary>
    public string? CommandId { get; init; }

    /// <summary>見出しの「＋」の読み上げとツールチップ（「新しい使い捨てリスト」）。</summary>
    public string? ActionName { get; init; }

    public string? IconKey { get; init; }

    public bool ShowCount { get; init; }

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private int _count;

    /// <summary>テーマに合わせたプロジェクト色。</summary>
    [ObservableProperty]
    private string? _colorHex;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    /// <summary>タスクをドラッグして重ねている（落とせる先だけ）。</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>タスクを落とせる項目か（プロジェクト＝そこへ移す、タグ＝そのタグを付ける）。</summary>
    public bool AcceptsTasks => Kind is NavItemKind.Project or NavItemKind.Tag;

    /// <summary>落とすときのゴーストの文言（「「業務改善」へ移す」「#仕事 を付ける」）。</summary>
    public string DropHint => Kind == NavItemKind.Project ? $"「{Label}」へ移す" : $"#{Label} を付ける";

    public bool IsSelectable => Kind is NavItemKind.View or NavItemKind.Project or NavItemKind.Tag or NavItemKind.SavedFilter or NavItemKind.Command or NavItemKind.Scratch;

    public bool HasIcon => IconKey is not null;

    public bool IsProject => Kind == NavItemKind.Project;

    public bool IsTag => Kind == NavItemKind.Tag;

    public bool IsScratch => Kind == NavItemKind.Scratch;

    public bool IsSection => Kind == NavItemKind.Section;

    /// <summary>見出しの右端に「＋」を出す（使い捨ての見出し）。</summary>
    public bool HasSectionAction => Kind == NavItemKind.Section && CommandId is not null;

    public bool IsSeparator => Kind == NavItemKind.Separator;

    public bool IsAddProject => Kind == NavItemKind.AddProject;

    public bool CanRename => Kind is NavItemKind.Project or NavItemKind.Tag;

    /// <summary>見出し・区切り以外（押せる行）。</summary>
    public bool IsRow => Kind is not (NavItemKind.Separator or NavItemKind.Section);

    /// <summary>名前の入力欄の読み上げ。</summary>
    public string EditName => Kind == NavItemKind.AddProject ? "新しいプロジェクトの名前" : $"「{Label}」の新しい名前";

    /// <summary>行の高さ（ビュー 36／プロジェクト・タグ 34）。</summary>
    public double RowHeight { get; init; } = 34;

    public IReadOnlyList<ColorChoice> ColorChoices { get; init; } = [];

    /// <summary>テーマ変換前のプロジェクト色（テーマが変わったら塗り直すため）。</summary>
    public string? SourceColorHex { get; init; }

    // ContextMenu からも使えるよう、コマンドは項目自身が持つ
    public IRelayCommand? InvokeCommand { get; init; }

    public IRelayCommand? BeginRenameCommand { get; init; }

    public IRelayCommand? ColorCommand { get; init; }

    public IRelayCommand? ArchiveCommand { get; init; }

    public IRelayCommand? DeleteCommand { get; init; }

    /// <summary>UI Automation の項目名（入れ物の既定の名前は ToString になるため）。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 左のサイドバー（UI 設計書 6.5）。ビュー・プロジェクト・タグ・保存済みフィルタと件数を出す。
/// 項目はプロジェクト・タグ・保存済みフィルタが変わったときだけ作り直し、タスクの変更では件数だけを読み直す。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    /// <summary>「使い捨て」の見出しの「＋」（新しい使い捨てリスト）。CommandInvoked で知らせる。</summary>
    public const string NewScratchCommandId = "newScratch";

    /// <summary>ProjectPalette.Light と同じ順の色の名前（色の変更メニュー用）。</summary>
    private static readonly string[] PaletteNames = ["青", "橙", "紫", "緑", "赤紫", "黄"];

    private readonly IProjectRepository _projects;
    private readonly ITagRepository _tags;
    private readonly ITaskRepository _tasks;
    private readonly ISettingsStore _settings;
    private readonly ThemeService _theme;
    private readonly ScratchStore _scratch;
    private readonly ILogger<SidebarViewModel> _logger;

    public SidebarViewModel(
        IProjectRepository projects,
        ITagRepository tags,
        ITaskRepository tasks,
        ISettingsStore settings,
        ThemeService theme,
        ScratchStore scratch,
        ILogger<SidebarViewModel> logger)
    {
        _projects = projects;
        _tags = tags;
        _tasks = tasks;
        _settings = settings;
        _theme = theme;
        _scratch = scratch;
        _logger = logger;
        _theme.ThemeChanged += (_, _) => RepaintProjectColors();
    }

    public ObservableCollection<NavItemViewModel> Items { get; } = [];

    public ObservableCollection<NavItemViewModel> BottomItems { get; } = [];

    /// <summary>最後に読み込んだ件数（空状態の文言などに使う）。</summary>
    public ViewCounts Counts { get; private set; } = ViewCounts.Empty;

    [ObservableProperty]
    private ViewKey _currentView = ViewKey.Today;

    /// <summary>ビューが選ばれた（プロジェクトを作った・いまのビューを消したときの移動もここから出す）。</summary>
    public event EventHandler<ViewKey>? ViewSelected;

    /// <summary>別画面を開く項目が押された（templates）。</summary>
    public event EventHandler<string>? CommandInvoked;

    /// <summary>画面下に短い知らせを出してほしい（同名のタグがあって名前を変えられなかった等）。</summary>
    public event EventHandler<string>? NoticeRequested;

    /// <summary>プロジェクト・タグにタスクが落とされた（中身の書き込みは一覧が受け持つ）。</summary>
    public event EventHandler<SidebarDrop>? TasksDropped;

    /// <summary>ドラッグで運んできたタスクを項目に落とす（プロジェクト・タグ以外には落とせない）。</summary>
    public void DropTasks(NavItemViewModel item, IReadOnlyList<Guid> taskIds)
    {
        item.IsDropTarget = false;
        if (item.AcceptsTasks && item.EntityId is not null && taskIds.Count > 0)
        {
            TasksDropped?.Invoke(this, new SidebarDrop(item, taskIds));
        }
    }

    /// <summary>ドラッグが終わった（Esc でやめた・外で離した）ので、重ねていた項目の強調を消す。</summary>
    public void ClearDropTargets()
    {
        foreach (var item in Items.Where(i => i.IsDropTarget))
        {
            item.IsDropTarget = false;
        }
    }

    /// <summary>項目と件数を読み直す。</summary>
    public async Task ReloadAsync()
    {
        var counts = await _tasks.GetViewCountsAsync();
        var projects = await _projects.GetAllAsync();
        var tags = await _tags.GetAllAsync();
        await _scratch.LoadAsync();
        Counts = counts;

        Items.Clear();
        Items.Add(ViewItem(ViewKey.Today, "今日", "IconToday", counts.Today, height: 36));
        Items.Add(ViewItem(ViewKey.Upcoming, "予定", "IconCalendar", counts.Upcoming, height: 36));
        Items.Add(ViewItem(ViewKey.All, "すべて", "IconList", counts.AllOpen, height: 36));

        // 使い捨てリスト（タスクとは別物なので件数は出さない）。無くても見出しと「＋」は出す
        Items.Add(new NavItemViewModel { Kind = NavItemKind.Separator });
        Items.Add(new NavItemViewModel
        {
            Kind = NavItemKind.Section,
            Label = "使い捨て",
            CommandId = NewScratchCommandId,
            ActionName = "新しい使い捨てリスト",
            InvokeCommand = SelectItemCommand,
        });
        foreach (var list in _scratch.Lists)
        {
            Items.Add(new NavItemViewModel
            {
                Kind = NavItemKind.Scratch,
                Key = ViewKey.ForScratch(list.Id),
                EntityId = list.Id,
                Label = list.Name,
                InvokeCommand = SelectItemCommand,
            });
        }

        Items.Add(new NavItemViewModel { Kind = NavItemKind.Separator });
        Items.Add(new NavItemViewModel { Kind = NavItemKind.Section, Label = "プロジェクト" });
        foreach (var project in projects)
        {
            Items.Add(new NavItemViewModel
            {
                Kind = NavItemKind.Project,
                Key = ViewKey.ForProject(project.Id),
                EntityId = project.Id,
                Label = project.Name,
                SourceColorHex = project.ColorHex,
                ColorHex = ProjectPalette.ForTheme(project.ColorHex, _theme.IsDark),
                Count = counts.ByProject.GetValueOrDefault(project.Id),
                ShowCount = true,
                ColorChoices =
                [
                    .. ProjectPalette.Light.Select((hex, i) => new ColorChoice(project.Id, hex, PaletteNames[i % PaletteNames.Length], SetProjectColorCommand)),
                ],
                InvokeCommand = SelectItemCommand,
                BeginRenameCommand = BeginRenameCommand,
                ColorCommand = SetProjectColorCommand,
                ArchiveCommand = ArchiveProjectCommand,
                DeleteCommand = DeleteProjectCommand,
            });
        }
        Items.Add(new NavItemViewModel
        {
            Kind = NavItemKind.AddProject,
            Label = "プロジェクトを追加",
            IconKey = "IconAdd",
            InvokeCommand = BeginAddProjectCommand,
        });

        if (tags.Count > 0)
        {
            Items.Add(new NavItemViewModel { Kind = NavItemKind.Separator });
            Items.Add(new NavItemViewModel { Kind = NavItemKind.Section, Label = "タグ" });
            foreach (var tag in tags)
            {
                Items.Add(new NavItemViewModel
                {
                    Kind = NavItemKind.Tag,
                    Key = ViewKey.ForTag(tag.Id),
                    EntityId = tag.Id,
                    Label = tag.Name,
                    Count = counts.ByTag.GetValueOrDefault(tag.Id),
                    ShowCount = true,
                    InvokeCommand = SelectItemCommand,
                    BeginRenameCommand = BeginRenameCommand,
                    DeleteCommand = DeleteTagCommand,
                });
            }
        }

        var filters = _settings.Current.SavedFilters;
        if (filters.Count > 0)
        {
            Items.Add(new NavItemViewModel { Kind = NavItemKind.Separator });
            Items.Add(new NavItemViewModel { Kind = NavItemKind.Section, Label = "保存済みフィルタ" });
            foreach (var filter in filters)
            {
                Items.Add(new NavItemViewModel
                {
                    Kind = NavItemKind.SavedFilter,
                    Key = ViewKey.ForSavedFilter(filter.Id),
                    EntityId = filter.Id,
                    Label = filter.Name,
                    IconKey = "IconFilter",
                    InvokeCommand = SelectItemCommand,
                });
            }
        }

        BottomItems.Clear();
        BottomItems.Add(ViewItem(ViewKey.Completed, "完了済み", "IconCheck", 0, showCount: false));
        BottomItems.Add(ViewItem(ViewKey.Trash, "ゴミ箱", "IconTrash", counts.Trash));
        BottomItems.Add(ViewItem(ViewKey.Stats, "振り返り", "IconChart", 0, showCount: false));
        BottomItems.Add(new NavItemViewModel
        {
            Kind = NavItemKind.Command,
            CommandId = "templates",
            Label = "テンプレート",
            IconKey = "IconTemplate",
            InvokeCommand = SelectItemCommand,
        });

        ApplySelection();
    }

    /// <summary>件数だけを読み直す（タスクが変わったとき）。</summary>
    public async Task RefreshCountsAsync()
    {
        var counts = await _tasks.GetViewCountsAsync();
        Counts = counts;
        foreach (var item in Items.Concat(BottomItems))
        {
            item.Count = item.Kind switch
            {
                NavItemKind.Project => counts.ByProject.GetValueOrDefault(item.EntityId ?? Guid.Empty),
                NavItemKind.Tag => counts.ByTag.GetValueOrDefault(item.EntityId ?? Guid.Empty),
                NavItemKind.View => item.Key?.Kind switch
                {
                    ViewKind.Today => counts.Today,
                    ViewKind.Upcoming => counts.Upcoming,
                    ViewKind.All => counts.AllOpen,
                    ViewKind.Trash => counts.Trash,
                    _ => item.Count,
                },
                _ => item.Count,
            };
        }
    }

    /// <summary>
    /// このビューがサイドバーにあるか（消したプロジェクト・タグ・フィルタ・捨てた使い捨てリストを起動時ビューにしていたときの確認）。
    /// 使い捨てリストは作った直後にも開けるよう、項目の作り直しを待たずにリストの保存役に聞く。
    /// </summary>
    public bool Contains(ViewKey key) => key.Kind switch
    {
        ViewKind.Scratch => key.Id is { } id && _scratch.Find(id) is not null,
        ViewKind.Project or ViewKind.Tag or ViewKind.SavedFilter => Items.Any(i => i.Key == key),
        _ => true,
    };

    /// <summary>見出しに出す名前（プロジェクト名・「#タグ名」・フィルタ名）。無ければ null。</summary>
    public string? LabelOf(ViewKey key)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is null)
        {
            return null;
        }
        return key.Kind == ViewKind.Tag ? "#" + item.Label : item.Label;
    }

    public void SetCurrentView(ViewKey key)
    {
        CurrentView = key;
        ApplySelection();
    }

    [RelayCommand]
    private void SelectItem(NavItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }
        if (item.CommandId is { } id)
        {
            CommandInvoked?.Invoke(this, id);
            return;
        }
        if (item.Key is { } key)
        {
            ViewSelected?.Invoke(this, key);
        }
    }

    [RelayCommand]
    private void BeginRename(NavItemViewModel? item)
    {
        if (item is null || !item.CanRename)
        {
            return;
        }
        item.EditText = item.Label;
        item.IsEditing = true;
    }

    [RelayCommand]
    private void BeginAddProject(NavItemViewModel? item)
    {
        item ??= Items.FirstOrDefault(i => i.Kind == NavItemKind.AddProject);
        if (item is null)
        {
            return;
        }
        item.EditText = "";
        item.IsEditing = true;
    }

    /// <summary>名前の入力を確定する（追加・改名の両方。Enter とフォーカスアウトから呼ばれるので2回目は何もしない）。</summary>
    public async Task CommitEditAsync(NavItemViewModel item)
    {
        if (!item.IsEditing)
        {
            return;
        }
        item.IsEditing = false;
        var text = TextNormalizer.ForName(item.EditText);
        if (text.Length == 0 || (item.CanRename && text == item.Label))
        {
            return;
        }
        ViewKey? moveTo = null;
        switch (item.Kind)
        {
            case NavItemKind.AddProject:
                var created = await _projects.AddAsync(text);
                _logger.LogInformation("プロジェクトを作りました {Id}", created.Id);
                moveTo = ViewKey.ForProject(created.Id);
                break;
            case NavItemKind.Project when item.EntityId is { } projectId:
                await _projects.UpdateAsync(projectId, p => p.Name = text);
                break;
            case NavItemKind.Tag when item.EntityId is { } tagId:
                var result = await _tags.RenameAsync(tagId, text);
                if (!result.Succeeded)
                {
                    _logger.LogWarning("タグの名前を変えられませんでした {Id}", tagId);
                    NoticeRequested?.Invoke(this, $"「{text}」というタグが既にあるため、名前を変えられませんでした");
                }
                break;
        }
        await ReloadAsync();
        if (moveTo is { } key)
        {
            // 作ったプロジェクトをそのまま開く（すぐにタスクを足せるように）
            ViewSelected?.Invoke(this, key);
        }
    }

    [RelayCommand]
    private async Task SetProjectColorAsync(ColorChoice? choice)
    {
        if (choice is null)
        {
            return;
        }
        await _projects.UpdateAsync(choice.ProjectId, p => p.ColorHex = choice.Hex);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ArchiveProjectAsync(NavItemViewModel? item)
    {
        if (item?.EntityId is not { } id)
        {
            return;
        }
        await _projects.UpdateAsync(id, p => p.IsArchived = true);
        await ReloadAsync();
        LeaveIfCurrent(ViewKey.ForProject(id));
    }

    /// <summary>プロジェクトを削除する（所属タスクは「プロジェクトなし」に移る。確認は View 側で取る）。</summary>
    [RelayCommand]
    private async Task DeleteProjectAsync(NavItemViewModel? item)
    {
        if (item?.EntityId is not { } id)
        {
            return;
        }
        await _projects.DeleteAsync(id);
        await ReloadAsync();
        LeaveIfCurrent(ViewKey.ForProject(id));
    }

    /// <summary>タグを削除する（タスクからは外れるがタスクは残る。確認は View 側で取る）。</summary>
    [RelayCommand]
    private async Task DeleteTagAsync(NavItemViewModel? item)
    {
        if (item?.EntityId is not { } id)
        {
            return;
        }
        await _tags.DeleteAsync(id);
        await ReloadAsync();
        LeaveIfCurrent(ViewKey.ForTag(id));
    }

    /// <summary>
    /// どの未削除タスクにも付いていないタグをまとめて削除する（F-045。確認は View 側で取る）。
    /// 消した件数を知らせる。いま開いているタグのビューが消えたら「今日」へ移る。
    /// </summary>
    [RelayCommand]
    private async Task DeleteUnusedTagsAsync()
    {
        var count = await _tags.DeleteUnusedAsync();
        _logger.LogInformation("使っていないタグを削除しました（{Count}件）", count);
        NoticeRequested?.Invoke(this, count > 0 ? $"使っていないタグを {count} 件削除しました" : "使っていないタグはありませんでした");
        await ReloadAsync();
        if (CurrentView.Kind == ViewKind.Tag && !Contains(CurrentView))
        {
            ViewSelected?.Invoke(this, ViewKey.Today);
        }
    }

    /// <summary>いま開いているビューが無くなったら「今日」へ移る。</summary>
    private void LeaveIfCurrent(ViewKey removed)
    {
        if (CurrentView == removed)
        {
            ViewSelected?.Invoke(this, ViewKey.Today);
        }
    }

    private NavItemViewModel ViewItem(ViewKey key, string label, string iconKey, int count, bool showCount = true, double height = 34) => new()
    {
        Kind = NavItemKind.View,
        Key = key,
        Label = label,
        IconKey = iconKey,
        Count = count,
        ShowCount = showCount,
        RowHeight = height,
        InvokeCommand = SelectItemCommand,
    };

    private void ApplySelection()
    {
        foreach (var item in Items.Concat(BottomItems))
        {
            item.IsSelected = item.Key == CurrentView;
        }
    }

    private void RepaintProjectColors()
    {
        foreach (var item in Items.Where(i => i.IsProject && i.SourceColorHex is not null))
        {
            item.ColorHex = ProjectPalette.ForTheme(item.SourceColorHex!, _theme.IsDark);
        }
    }
}
