using System.Globalization;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Views.Palette;

/// <summary>
/// パレットに並べる行の元（UI 設計書 11.6 のコマンド、移動先、テンプレート、記号の案内）。開いたときに1回だけ作る。
/// コマンドは並べた順が「同点なら定義順」の順で、使ったことが無いときの「よく使うコマンド」もこの頭の3件になる。
/// 同期（Phase 5）とサイドバーの表示切替（ShellService に口が無い）は出さない。
/// </summary>
public static class PaletteCatalog
{
    /// <summary>ヘルプ（?）でショートカット一覧の次に並べる記号の案内。</summary>
    public static IReadOnlyList<(string Symbol, string Name)> Prefixes { get; } =
    [
        (">", "コマンドだけに絞る"),
        ("#", "タグを探す"),
        ("@", "プロジェクトを探す"),
        ("/", "ビューを移動する"),
        ("+", "テンプレートを探して作る"),
    ];

    public static IReadOnlyList<PaletteTarget> Build(
        IReadOnlyList<Project> projects,
        IReadOnlyList<Tag> tags,
        IReadOnlyList<SavedFilter> filters,
        IReadOnlyList<TemplateSummary> templates,
        ViewCounts counts)
    {
        var list = new List<PaletteTarget>();
        var order = 0;

        PaletteTarget Command(PaletteCommand command, string name, string icon, string keywords, string? parameter = null, string? keyHint = null, string? detail = null) =>
            new(PaletteItemKind.Command, "command:" + command.ToString().ToLowerInvariant() + (parameter is null ? "" : ":" + parameter), name, order++)
            {
                Command = command,
                Parameter = parameter,
                IconKey = icon,
                Keywords = keywords,
                KeyHint = keyHint,
                Detail = detail,
            };

        list.Add(Command(PaletteCommand.CarryOver, "期限切れをまとめて今日へ繰り越す", "IconCalendar", "くりこし 繰越 期限切れ 延期 今日へ carry overdue", detail: Count(counts.Overdue)));
        list.Add(Command(PaletteCommand.OpenTemplates, "テンプレートから作る", "IconTemplate", "てんぷれーと 展開 template", keyHint: "Ctrl+T"));
        list.Add(Command(PaletteCommand.FocusMode, "フォーカスモードを始める", "IconFocus", "ふぉーかす 集中 1件ずつ focus", keyHint: "Ctrl+Shift+F"));
        list.Add(Command(PaletteCommand.Settings, "設定を開く", "IconSettings", "せってい 環境設定 オプション settings preferences", keyHint: "Ctrl+,"));
        list.Add(Command(PaletteCommand.Shortcuts, "ショートカット一覧を見る", "IconKeyboard", "しょーとかっと キー 一覧 ヘルプ help keys", keyHint: "?"));
        list.Add(Command(PaletteCommand.ToggleTheme, "テーマを切り替える", "IconTheme", "てーま ダーク ライト 外観 色 theme dark light"));
        list.Add(Command(PaletteCommand.DeleteCompleted, "完了済みを一括削除", "IconTrash", "かんりょう 完了済み ゴミ箱 片付け まとめて削除 delete done"));
        list.Add(Command(PaletteCommand.Backup, "バックアップを取る", "IconDatabase", "ばっくあっぷ 保存 backup"));
        list.Add(Command(PaletteCommand.OpenDataFolder, "保存先フォルダを開く", "IconFolder", "ほぞんさき フォルダ データ エクスプローラー folder"));
        list.Add(Command(PaletteCommand.Settings, "設定: 全般", "IconMonitor", "せってい 全般 起動 自動起動 general", "general"));
        list.Add(Command(PaletteCommand.Settings, "設定: 外観", "IconTheme", "せってい 外観 テーマ アクセント 色 appearance", "appearance"));
        list.Add(Command(PaletteCommand.Settings, "設定: 通知", "IconBell", "せってい 通知 リマインダー 日次サマリ notifications", "notifications"));
        list.Add(Command(PaletteCommand.Settings, "設定: ショートカット", "IconKeyboard", "せってい ホットキー キー hotkey shortcuts", "shortcuts"));
        list.Add(Command(PaletteCommand.Settings, "設定: 外部サービス", "IconSync", "せってい 外部 祝日 天気 更新 external", "external"));
        list.Add(Command(PaletteCommand.Settings, "設定: データ", "IconDatabase", "せってい データ バックアップ 復元 インポート エクスポート data", "data"));
        list.Add(Command(PaletteCommand.Settings, "設定: TaskDeck について", "IconInfo", "せってい バージョン 更新 about", "about"));

        foreach (var template in templates)
        {
            list.Add(new PaletteTarget(PaletteItemKind.Template, "template:" + template.Template.Id.ToString("N"), template.Template.Name, order++)
            {
                LabelPrefix = "テンプレート「",
                LabelSuffix = "」から作る",
                IconKey = "IconTemplate",
                Detail = Count(template.ItemCount),
                EntityId = template.Template.Id,
            });
        }

        foreach (var project in projects)
        {
            list.Add(new PaletteTarget(PaletteItemKind.Project, "project:" + project.Id.ToString("N"), project.Name, order++)
            {
                LabelPrefix = "プロジェクト「",
                LabelSuffix = "」を開く",
                ColorHex = project.ColorHex,
                Detail = Count(counts.ByProject.GetValueOrDefault(project.Id)),
                View = ViewKey.ForProject(project.Id),
                EntityId = project.Id,
            });
        }

        foreach (var tag in tags)
        {
            list.Add(new PaletteTarget(PaletteItemKind.Tag, "tag:" + tag.Id.ToString("N"), tag.Name, order++)
            {
                LabelPrefix = "タグ「#",
                LabelSuffix = "」を開く",
                IconKey = "IconTag",
                Detail = Count(counts.ByTag.GetValueOrDefault(tag.Id)),
                View = ViewKey.ForTag(tag.Id),
                EntityId = tag.Id,
            });
        }

        PaletteTarget View(ViewKey key, string icon, string keywords, string? keyHint, int? count) =>
            new(PaletteItemKind.View, "view:" + key.Kind.ToString().ToLowerInvariant(), BuiltInViews.TitleOf(key.Kind), order++)
            {
                LabelPrefix = "「",
                LabelSuffix = "」を開く",
                IconKey = icon,
                Keywords = keywords,
                KeyHint = keyHint,
                Detail = count is { } n ? Count(n) : null,
                View = key,
            };

        list.Add(View(ViewKey.Today, "IconToday", "きょう today", "Ctrl+1", counts.Today));
        list.Add(View(ViewKey.Upcoming, "IconCalendar", "よてい 今週 7日 upcoming", "Ctrl+2", counts.Upcoming));
        list.Add(View(ViewKey.All, "IconList", "すべて 全部 ぜんぶ all", "Ctrl+3", counts.AllOpen));
        list.Add(View(ViewKey.Completed, "IconCheck", "かんりょう 終わった done completed", "Ctrl+4", null));
        list.Add(View(ViewKey.Calendar, "IconCalendarDay", "かれんだー 月 週 切り替える calendar", "Ctrl+5", null));
        list.Add(View(ViewKey.Stats, "IconChart", "ふりかえり 統計 グラフ stats", "Ctrl+6", null));
        list.Add(View(ViewKey.Trash, "IconTrash", "ごみばこ 削除 trash", null, counts.Trash));

        foreach (var filter in filters)
        {
            list.Add(new PaletteTarget(PaletteItemKind.SavedFilter, "filter:" + filter.Id.ToString("N"), filter.Name, order++)
            {
                LabelPrefix = "フィルタ「",
                LabelSuffix = "」を開く",
                IconKey = "IconFilter",
                Keywords = "ふぃるた 絞り込み filter",
                View = ViewKey.ForSavedFilter(filter.Id),
                EntityId = filter.Id,
            });
        }

        foreach (var (symbol, name) in Prefixes)
        {
            list.Add(new PaletteTarget(PaletteItemKind.Prefix, "prefix:" + symbol, name, order++) { Parameter = symbol });
        }
        return list;
    }

    private static string Count(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} 件");
}
