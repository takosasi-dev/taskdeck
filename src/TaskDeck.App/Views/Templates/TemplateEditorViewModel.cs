using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.App.Controls.Pickers;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Views.Templates;

/// <summary>
/// 編集中の項目1つ。日数・時刻は入力のまま文字で持ち、保存のときに読む（読めなければ保存しない）。
/// 画面に出さない値（通知・所要時間）も持ち回り、保存で消さない。
/// </summary>
public sealed partial class EditorItem : ObservableObject
{
    public EditorItem(Guid? id = null) => Id = id ?? Guid.CreateVersion7();

    public Guid Id { get; }

    /// <summary>0〜2（親→子→孫）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Indent))]
    private int _level;

    [ObservableProperty]
    private string _title = "";

    /// <summary>基準日からの日数（"-3" で3日前、"" で期限なし）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueSummary), nameof(OffsetHint), nameof(HasOffsetError))]
    private string _offsetText = "";

    /// <summary>"20:00"。"" で終日。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueSummary), nameof(TimeHint))]
    private string _timeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityMarks))]
    private Priority _priority;

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private IReadOnlyList<Guid> _tagIds = [];

    public int? RemindOffsetMinutes { get; init; }

    public int? DurationMinutes { get; init; }

    public double Indent => Level * 22;

    public string PriorityMarks => ExpansionRow.Marks(Priority);

    /// <summary>行の右に出す「3日前 20:00」。期限なし・読めないときは空。</summary>
    public string DueSummary
    {
        get
        {
            if (!TryGetOffset(out var offset) || offset is not { } days)
            {
                return "";
            }
            var time = TryGetTime(out var t) && t is { } at ? " " + DateLabels.Time(at) : "";
            return TemplateExpander.DescribeOffset(days) + time;
        }
    }

    public string OffsetHint => TryGetOffset(out var offset)
        ? offset is { } days ? TemplateExpander.DescribeOffset(days) : "期限なし"
        : "日数が読めません（例: -3 で3日前）";

    public bool HasOffsetError => !TryGetOffset(out _);

    public string? TimeHint => TryGetTime(out _) ? null : "時刻が読めません（例: 20:00）";

    public static EditorItem From(TaskTemplateItem item, int level) => new(item.Id)
    {
        Level = level,
        Title = item.Title,
        OffsetText = item.DueOffsetDays?.ToString(CultureInfo.InvariantCulture) ?? "",
        TimeText = item.DueTime is { } time ? DateLabels.Time(time) : "",
        Priority = item.Priority,
        Notes = item.Notes ?? "",
        TagIds = [.. item.TagIds],
        RemindOffsetMinutes = item.RemindOffsetMinutes,
        DurationMinutes = item.DurationMinutes,
    };

    /// <summary>空なら期限なし（null）。全角も受ける。±10年まで。</summary>
    public bool TryGetOffset(out int? days)
    {
        days = null;
        var text = TextNormalizer.ForName(OffsetText);
        if (text.Length == 0)
        {
            return true;
        }
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && Math.Abs(value) <= 3650)
        {
            days = value;
            return true;
        }
        return false;
    }

    /// <summary>空なら終日（null）。"20:00" "7:00"（全角も可）。</summary>
    public bool TryGetTime(out TimeOnly? time)
    {
        time = null;
        var text = TextNormalizer.ForName(TimeText);
        if (text.Length == 0)
        {
            return true;
        }
        if (TimeOnly.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            time = value;
            return true;
        }
        return false;
    }
}

/// <summary>
/// テンプレートの新規作成・編集（F-151・F-153・F-159）。
/// 項目はアウトライナーと同じく「並び＋深さ」の平らな一覧で持つ（親は、直前にある1段浅い項目）。
/// 並べ替え・字下げは兄弟の単位（自分と子孫をひとかたまり）で動かす。保存の形（ParentItemId・Depth・SortOrder）には TryBuild で直す。
/// </summary>
public sealed partial class TemplateEditorViewModel : ObservableObject
{
    public const int MaxLevel = TaskItem.MaxDepth;

    private readonly TaskTemplate? _source;
    private readonly IReadOnlyList<Tag> _allTags;
    private readonly IReadOnlyDictionary<Guid, Project> _projects;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _description;

    /// <summary>基準日の呼び名（「出発日」）。空なら「基準日」。</summary>
    [ObservableProperty]
    private string _anchorLabel;

    [ObservableProperty]
    private string _iconKey;

    [ObservableProperty]
    private string _colorHex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultProjectName), nameof(DefaultProjectColor))]
    private Guid? _defaultProjectId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddChildCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(IndentCommand), nameof(OutdentCommand), nameof(RemoveCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private EditorItem? _selectedItem;

    [ObservableProperty]
    private string? _error;

    public TemplateEditorViewModel(
        TaskTemplate? source,
        IReadOnlyList<TaskTemplateItem> items,
        IReadOnlyList<Tag> allTags,
        IReadOnlyDictionary<Guid, Project> projects)
    {
        _source = source;
        _allTags = allTags;
        _projects = projects;
        _name = source?.Name ?? "";
        _description = source?.Description ?? "";
        _anchorLabel = source?.AnchorLabel ?? "";
        _iconKey = source?.IconKey ?? IconChoices[0];
        _colorHex = source?.ColorHex ?? ColorChoices[0];
        _defaultProjectId = source?.DefaultProjectId;
        DefaultTags.Reset(allTags, source?.DefaultTagIds ?? []);
        foreach (var (item, level) in TemplateTree.Flatten(items))
        {
            Items.Add(EditorItem.From(item, level));
        }
        if (Items.Count == 0)
        {
            Items.Add(new EditorItem());
        }
        ItemTags.Changed += (_, _) =>
        {
            if (SelectedItem is { } selected)
            {
                selected.TagIds = ItemTags.SelectedIds;
            }
        };
        SelectedItem = Items[0];
    }

    /// <summary>テンプレートのアイコン（Resources/Icons.xaml のキー）。</summary>
    public static IReadOnlyList<string> IconChoices { get; } =
    [
        "IconCalendar", "IconList", "IconPlus", "IconBriefcase", "IconMonitor", "IconPerson",
        "IconTemplate", "IconCheckCircle", "IconFlag", "IconNote", "IconBell", "IconSun",
    ];

    /// <summary>テンプレートの色（プロジェクトと同じ検証済みの6色）。</summary>
    public static IReadOnlyList<string> ColorChoices { get; } = ProjectPalette.Light;

    public static IReadOnlyList<PickerOption<Priority>> PriorityOptions { get; } =
    [
        new(Priority.None, "なし"),
        new(Priority.Low, "! 低"),
        new(Priority.Medium, "!! 中"),
        new(Priority.High, "!!! 高"),
        new(Priority.Urgent, "!!!! 緊急"),
    ];

    public ObservableCollection<EditorItem> Items { get; } = [];

    /// <summary>テンプレートの既定のタグ（展開のたびに付く）。</summary>
    public TagSelection DefaultTags { get; } = new();

    /// <summary>選んでいる項目のタグ。</summary>
    public TagSelection ItemTags { get; } = new();

    public bool IsNew => _source is null;

    public string Heading => IsNew ? "新しいテンプレート" : "テンプレートを編集";

    public bool HasSelection => SelectedItem is not null;

    public string DefaultProjectName =>
        DefaultProjectId is { } id && _projects.TryGetValue(id, out var project) ? project.Name : "なし";

    public string? DefaultProjectColor =>
        DefaultProjectId is { } id && _projects.TryGetValue(id, out var project) ? project.ColorHex : null;

    /// <summary>親の画面がプロジェクトを読み直したあとに名前を出し直す。</summary>
    public void RefreshProjectLabel()
    {
        OnPropertyChanged(nameof(DefaultProjectName));
        OnPropertyChanged(nameof(DefaultProjectColor));
    }

    /// <summary>テーマが変わった。色（アイコンの線・プロジェクトの点）はコンバータが作るので、作り直させる。</summary>
    public void RefreshThemeColors()
    {
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(DefaultProjectColor));
    }

    /// <summary>
    /// 保存する形に直す。名前が空・日数や時刻が読めない・空のタイトルにサブ項目がある、なら false（Error に理由）。
    /// タイトルが空で子の無い行は、打ちかけの行として保存しない。
    /// </summary>
    public bool TryBuild(out TaskTemplate template, out IReadOnlyList<TaskTemplateItem> items)
    {
        template = null!;
        items = [];
        var name = TextNormalizer.ForName(Name);
        if (name.Length == 0)
        {
            Error = "名前を入れてください";
            return false;
        }

        var result = new List<TaskTemplateItem>();
        var parents = new Guid?[MaxLevel + 1];
        var counters = new Dictionary<Guid, int>();
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                if (i + 1 < Items.Count && Items[i + 1].Level > item.Level)
                {
                    return Reject(item, "タイトルが空の項目にサブ項目があります");
                }
                continue;
            }
            if (!item.TryGetOffset(out var offset))
            {
                return Reject(item, $"「{item.Title.Trim()}」の日数が読めません（例: -3 で3日前）");
            }
            if (!item.TryGetTime(out var time))
            {
                return Reject(item, $"「{item.Title.Trim()}」の時刻が読めません（例: 20:00）");
            }
            var parentId = item.Level == 0 ? null : parents[item.Level - 1];
            var key = parentId ?? Guid.Empty;
            var order = counters[key] = counters.GetValueOrDefault(key) + 1;
            result.Add(new TaskTemplateItem
            {
                Id = item.Id,
                Title = item.Title.Trim(),
                Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes,
                Priority = item.Priority,
                DueOffsetDays = offset,
                DueTime = time,
                RemindOffsetMinutes = item.RemindOffsetMinutes,
                DurationMinutes = item.DurationMinutes,
                ParentItemId = parentId,
                Depth = item.Level,
                TagIds = [.. item.TagIds],
                SortOrder = order * SortOrderMath.Step,
            });
            parents[item.Level] = item.Id;
        }
        if (result.Count == 0)
        {
            Error = "項目を1つ以上入れてください";
            return false;
        }

        template = new TaskTemplate
        {
            Id = _source?.Id ?? Guid.CreateVersion7(),
            Name = name,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            AnchorLabel = string.IsNullOrWhiteSpace(AnchorLabel) ? null : TextNormalizer.ForName(AnchorLabel),
            IconKey = IconKey,
            ColorHex = ColorHex,
            DefaultProjectId = DefaultProjectId,
            DefaultTagIds = [.. DefaultTags.SelectedIds],
            UseCount = _source?.UseCount ?? 0,
            LastUsedAt = _source?.LastUsedAt,
            SortOrder = _source?.SortOrder ?? 0,
        };
        items = result;
        Error = null;
        return true;
    }

    /// <summary>選んでいる項目の後ろ（子孫の後ろ）に同じ深さの項目を足す。何も選んでいなければ末尾。</summary>
    [RelayCommand]
    private void AddItem()
    {
        if (SelectedItem is not { } selected)
        {
            Insert(Items.Count, new EditorItem());
            return;
        }
        var index = Items.IndexOf(selected);
        Insert(SubtreeEnd(index) + 1, new EditorItem { Level = selected.Level });
    }

    /// <summary>選んでいる項目の最後の子として足す（3階層まで）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddChild))]
    private void AddChild()
    {
        var index = Items.IndexOf(SelectedItem!);
        Insert(SubtreeEnd(index) + 1, new EditorItem { Level = SelectedItem!.Level + 1 });
    }

    private bool CanAddChild() => SelectedItem is { Level: < MaxLevel };

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        var index = Items.IndexOf(SelectedItem!);
        MoveBlock(index, SubtreeEnd(index), PreviousSibling(index));
    }

    private bool CanMoveUp() => SelectedItem is { } s && PreviousSibling(Items.IndexOf(s)) >= 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        var index = Items.IndexOf(SelectedItem!);
        var next = NextSibling(index);
        MoveBlock(next, SubtreeEnd(next), index);   // 次の兄弟（と子孫）を自分の前へ
    }

    private bool CanMoveDown() => SelectedItem is { } s && NextSibling(Items.IndexOf(s)) >= 0;

    /// <summary>1段下げる（直前の兄弟の最後の子になる）。子孫も一緒に下がる。3階層を超えるなら下げない。</summary>
    [RelayCommand(CanExecute = nameof(CanIndent))]
    private void Indent()
    {
        var index = Items.IndexOf(SelectedItem!);
        var end = SubtreeEnd(index);   // 深さを変える前に範囲を決める
        for (var i = index; i <= end; i++)
        {
            Items[i].Level++;
        }
        NotifyCommands();
    }

    private bool CanIndent()
    {
        if (SelectedItem is not { } selected)
        {
            return false;
        }
        var index = Items.IndexOf(selected);
        var deepest = Items.Skip(index).Take(SubtreeEnd(index) - index + 1).Max(i => i.Level);
        return PreviousSibling(index) >= 0 && deepest < MaxLevel;
    }

    /// <summary>1段上げる（親の子孫の後ろへ出る）。子孫も一緒に上がる。</summary>
    [RelayCommand(CanExecute = nameof(CanOutdent))]
    private void Outdent()
    {
        var index = Items.IndexOf(SelectedItem!);
        var end = SubtreeEnd(index);
        var size = end - index + 1;
        var parentEnd = SubtreeEnd(ParentOf(index));
        var block = Items.Skip(index).Take(size).ToList();
        MoveBlock(index, end, parentEnd - size + 1);
        foreach (var item in block)
        {
            item.Level--;
        }
        NotifyCommands();
    }

    private bool CanOutdent() => SelectedItem is { Level: > 0 };

    /// <summary>項目と子孫を消す（テンプレートは保存するまで書き換わらない）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var index = Items.IndexOf(SelectedItem!);
        for (var i = SubtreeEnd(index); i >= index; i--)
        {
            Items.RemoveAt(i);
        }
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    partial void OnSelectedItemChanged(EditorItem? value) => ItemTags.Reset(_allTags, value?.TagIds ?? []);

    private bool Reject(EditorItem item, string message)
    {
        Error = message;
        SelectedItem = item;
        return false;
    }

    private void Insert(int index, EditorItem item)
    {
        Items.Insert(index, item);
        SelectedItem = item;
        NotifyCommands();
    }

    /// <summary>自分の子孫の最後の位置（子孫が無ければ自分）。</summary>
    private int SubtreeEnd(int index)
    {
        var level = Items[index].Level;
        var end = index;
        while (end + 1 < Items.Count && Items[end + 1].Level > level)
        {
            end++;
        }
        return end;
    }

    private int PreviousSibling(int index)
    {
        var level = Items[index].Level;
        for (var i = index - 1; i >= 0; i--)
        {
            if (Items[i].Level < level)
            {
                return -1;
            }
            if (Items[i].Level == level)
            {
                return i;
            }
        }
        return -1;
    }

    private int NextSibling(int index)
    {
        var next = SubtreeEnd(index) + 1;
        return next < Items.Count && Items[next].Level == Items[index].Level ? next : -1;
    }

    private int ParentOf(int index)
    {
        var level = Items[index].Level;
        for (var i = index - 1; i >= 0; i--)
        {
            if (Items[i].Level < level)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>[start..end] を抜き出して、抜いたあとの位置 target に入れる。選択は保つ。</summary>
    private void MoveBlock(int start, int end, int target)
    {
        var selected = SelectedItem;
        var block = Items.Skip(start).Take(end - start + 1).ToList();
        for (var i = end; i >= start; i--)
        {
            Items.RemoveAt(i);
        }
        for (var i = 0; i < block.Count; i++)
        {
            Items.Insert(target + i, block[i]);
        }
        SelectedItem = selected;
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        AddChildCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        IndentCommand.NotifyCanExecuteChanged();
        OutdentCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }
}
