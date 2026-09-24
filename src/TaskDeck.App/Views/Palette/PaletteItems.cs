using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;

namespace TaskDeck.App.Views.Palette;

/// <summary>パレットの行の種類。</summary>
public enum PaletteItemKind
{
    Task,
    /// <summary>入力した文字でタスクを作る（Ctrl+Enter と同じ）。</summary>
    CreateTask,
    Command,
    /// <summary>テンプレートから作る（F-15E）。</summary>
    Template,
    Project,
    Tag,
    View,
    SavedFilter,
    /// <summary>絞り込み記号を入れる（ヘルプの中の案内）。</summary>
    Prefix,
    /// <summary>「さらに N 件」。押すとそのセクションを広げる。</summary>
    More,
}

/// <summary>コマンドの中身（UI 設計書 11.6。同期は Phase 5 なので出さない）。</summary>
public enum PaletteCommand
{
    None,
    CarryOver,
    OpenTemplates,
    FocusMode,
    Settings,
    Shortcuts,
    ToggleTheme,
    DeleteCompleted,
    Backup,
    OpenDataFolder,
}

/// <summary>
/// 選べる行の元（コマンド・移動先・テンプレート・記号の案内）。検索のたびに一致を調べて <see cref="PaletteItem"/> にする。
/// 行の文は LabelPrefix + Name + LabelSuffix で、一致を調べるのは Name（と Keywords）だけ。
/// Key は使用頻度の記録に使う（"command:settings" "project:{Id}" など）。Order は同点のときの定義順。
/// </summary>
public sealed record PaletteTarget(PaletteItemKind Kind, string Key, string Name, int Order)
{
    public string LabelPrefix { get; init; } = "";
    public string LabelSuffix { get; init; } = "";
    /// <summary>読み・別名（ここに当たったら部分一致として出す）。</summary>
    public string? Keywords { get; init; }
    public string? IconKey { get; init; }
    /// <summary>プロジェクトの色（テーマ変換前の #RRGGBB）。</summary>
    public string? ColorHex { get; init; }
    /// <summary>右に出す補足（「5 件」など）。</summary>
    public string? Detail { get; init; }
    /// <summary>右に出すキー（「Ctrl+,」など）。</summary>
    public string? KeyHint { get; init; }
    public PaletteCommand Command { get; init; }
    /// <summary>設定のページ名・入れる記号・広げるセクション。</summary>
    public string? Parameter { get; init; }
    public ViewKey? View { get; init; }
    public Guid? EntityId { get; init; }

    public string Label => LabelPrefix + Name + LabelSuffix;
}

/// <summary>Tab で絞り込んだ先（プロジェクトかタグ）。これがある間は、入力した文字でその中のタスクを探す。</summary>
public sealed record PaletteScope(PaletteItemKind Kind, Guid Id, string Name, string? ColorHex)
{
    public string Label => Kind == PaletteItemKind.Tag ? "#" + Name : Name;

    public bool IsProject => Kind == PaletteItemKind.Project;
}

/// <summary>パレットの結果の1行（見出し・案内・選べる行）。</summary>
public abstract class PaletteRow : ObservableObject;

/// <summary>セクションの見出し（「タスク」「コマンド」「移動」）。2つ目からは上に区切り線を引く。</summary>
public sealed class PaletteHeaderRow(string title, bool hasDivider) : PaletteRow
{
    public string Title { get; } = title;

    public bool HasDivider { get; } = hasDivider;

    /// <summary>UI Automation は一覧の項目の名前に ToString を使う。</summary>
    public override string ToString() => Title;
}

/// <summary>選べない案内（「ほか 120 件。語を足して絞り込めます」）。</summary>
public sealed class PaletteNoteRow(string text) : PaletteRow
{
    public string Text { get; } = text;

    public override string ToString() => Text;
}

/// <summary>↑↓ で選べる行。</summary>
public abstract partial class PaletteEntry : PaletteRow
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>読み直した後も同じ行を選んだままにするための鍵。</summary>
    public abstract string Key { get; }

    public abstract string AutomationName { get; }

    public override string ToString() => AutomationName;
}

/// <summary>コマンド・移動先・テンプレート・記号の案内・「さらに N 件」の行。</summary>
public sealed class PaletteItem(PaletteTarget target, IReadOnlyList<TextPiece> pieces) : PaletteEntry
{
    public PaletteTarget Target { get; } = target;

    public PaletteItemKind Kind => Target.Kind;

    public override string Key => Target.Key;

    public IReadOnlyList<TextPiece> Pieces { get; } = pieces;

    public string Label => Target.Label;

    public string? Detail => Target.Detail;

    public bool HasDetail => !string.IsNullOrEmpty(Target.Detail);

    public string? KeyHint => Target.KeyHint;

    public bool HasKeyHint => !string.IsNullOrEmpty(Target.KeyHint);

    public string? IconKey => Target.IconKey;

    public bool HasIcon => Target.IconKey is not null;

    public string? ColorHex => Target.ColorHex;

    public bool HasColor => Target.ColorHex is not null;

    /// <summary>絞り込み記号の案内（左に記号をキーの形で出す）。</summary>
    public bool IsPrefix => Kind == PaletteItemKind.Prefix;

    public string? PrefixSymbol => IsPrefix ? Target.Parameter : null;

    /// <summary>アクセント色で出す行（入力から作る・さらに N 件）。</summary>
    public bool IsAccent => Kind is PaletteItemKind.CreateTask or PaletteItemKind.More;

    public override string AutomationName => HasDetail ? Label + " " + Detail : Label;
}

/// <summary>タスクの行（チェック・優先度・タイトル・プロジェクト・期限）。Space とチェックで完了を切り替える。</summary>
public sealed partial class PaletteTaskItem : PaletteEntry
{
    public required TaskItem Task { get; init; }

    public Guid TaskId => Task.Id;

    public override string Key => "task:" + Task.Id.ToString("N");

    public string Title => Task.Title;

    public required IReadOnlyList<TextPiece> Pieces { get; init; }

    public Priority Priority => Task.Priority;

    public string PrioritySymbol => DisplayText.PrioritySymbol(Task.Priority);

    public string? ProjectName { get; init; }

    public bool HasProject => !string.IsNullOrEmpty(ProjectName);

    /// <summary>「今日 15:00」「9/23」「3日超過」、閉じたものは閉じた日。</summary>
    public required string DueText { get; init; }

    public bool IsOverdue { get; init; }

    /// <summary>今日の時刻つき（濃く出す）。</summary>
    public bool IsDueStrong { get; init; }

    /// <summary>完了か中止（チェックの付いた見た目。パレットの中で切り替えるとここが変わる）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClosedText))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    [NotifyPropertyChangedFor(nameof(CheckName))]
    private bool _isClosed;

    public string? ClosedText => IsClosed ? (Task.Status == TaskItemStatus.Cancelled ? "中止" : "完了済み") : null;

    public string CheckName => Title + (IsClosed ? " を未完了に戻す" : " を完了にする");

    public override string AutomationName => (IsClosed ? "完了済み、" : "") + Title + (DueText.Length > 0 ? "、" + DueText : "");

    /// <summary>取りやめた操作のあと、チェックの見た目を状態に合わせ直す（クリックで先に動いた分を戻す）。</summary>
    public void RefreshCheck() => OnPropertyChanged(nameof(IsClosed));
}
