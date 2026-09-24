using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.App.Views.Palette;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Views.Shortcuts;

/// <summary>キーの表記の1片（キー1つの枠・間の「+」・言い換えの「／」）。</summary>
public enum KeyTokenKind
{
    Cap,
    Plus,
    Or,
}

/// <summary>キーの表記の1片。IsAccent はコマンドパレットと「?」だけ（UI 設計書 21.2）。</summary>
public sealed record KeyToken(string Text, KeyTokenKind Kind, bool IsAccent)
{
    public bool IsCap => Kind == KeyTokenKind.Cap;

    /// <summary>UI Automation は一覧の項目の名前に ToString を使う。</summary>
    public override string ToString() => Text;
}

/// <summary>ショートカット一覧の1行（動作名は左、キーは右寄せ）。</summary>
public sealed partial class ShortcutRow : ObservableObject
{
    private readonly string _searchText;

    /// <param name="action">動作名。</param>
    /// <param name="keys">「Ctrl+Shift+Space」。言い換えは「|」で区切る（「Tab|Shift+Tab」）。</param>
    /// <param name="keywords">動作名に無い言い方（「削除」で「ゴミ箱に入れる」を見つけるため）。</param>
    /// <param name="isEditable">設定で変えられる（グローバルホットキー。鉛筆を添える）。</param>
    /// <param name="isAccent">目立たせる（コマンドパレットと「?」）。</param>
    public ShortcutRow(string action, string keys, string keywords = "", bool isEditable = false, bool isAccent = false)
    {
        Action = action;
        KeysText = keys.Length == 0 ? "未設定" : keys.Replace("|", " または ", StringComparison.Ordinal);
        Keys = ShortcutsViewModel.ParseKeys(keys.Length == 0 ? "未設定" : keys, isAccent);
        IsEditable = isEditable;
        IsAccent = isAccent;
        _searchText = PaletteText.Fold(action + " " + keywords);
    }

    public string Action { get; }

    public IReadOnlyList<KeyToken> Keys { get; }

    /// <summary>読み上げ用のキー（「Ctrl+K」「Tab または Shift+Tab」）。</summary>
    public string KeysText { get; }

    public bool IsEditable { get; }

    public bool IsAccent { get; }

    public string AutomationName => Action + "、" + KeysText + (IsEditable ? "（設定で変更できます）" : "");

    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>検索語（折りたたみ済み）が全部、動作名か言い換えに含まれるか。キーの名前では探さない（UI 設計書 21.1）。</summary>
    internal bool Matches(IReadOnlyList<string> terms) => terms.All(t => _searchText.Contains(t, StringComparison.Ordinal));

    public override string ToString() => AutomationName;
}

/// <summary>カテゴリ（見出しと区切り線の下に行が並ぶ。Note は行の下に添える補足）。</summary>
public sealed partial class ShortcutCategory(string title, IReadOnlyList<ShortcutRow> rows, bool isAccent = false, string? note = null) : ObservableObject
{
    public string Title { get; } = title;

    public IReadOnlyList<ShortcutRow> Rows { get; } = rows;

    /// <summary>「どのアプリからでも」だけ見出しをアクセント色にする（OS で効くものとアプリ内だけのものを分ける）。</summary>
    public bool IsAccent { get; } = isAccent;

    public string? Note { get; } = note;

    public bool HasNote => Note is not null;

    [ObservableProperty]
    private bool _isVisible = true;

    public override string ToString() => Title;
}

/// <summary>
/// ショートカット一覧（S-12、UI 設計書 21）。担当: 波3-J。
/// アプリ内のキーはメイン画面の実装（MainWindow の KeyBinding・一覧と詳細ペインのキー処理）を正本にして、実装されていないものは載せない
/// （モックアップにある Ctrl+; ・Ctrl+Shift+P・Ctrl+Shift+G・Ctrl+R・Ctrl+S はメイン画面に無いので出さない）。
/// グローバルホットキーは設定（settings.Hotkeys）の値を出すだけ（変更は設定の「ショートカット」ページ）。
/// 標準の操作（Ctrl+A・Ctrl+C など）・方向キーの移動・マウス操作は載せない（UI 設計書 21.4）。
/// ただし使い捨てリストの ↑↓ は「最後の行で↓を押すと追加の欄へ」という試しても分かりにくい動きがあるので載せる。
/// </summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    public ShortcutsViewModel(ISettingsStore settings)
    {
        var hotkeys = settings.Current.Hotkeys;
        LeftColumn =
        [
            new ShortcutCategory("どのアプリからでも",
            [
                new ShortcutRow("クイック入力を開く", hotkeys.QuickInput, "追加 作る 新しい", isEditable: true),
                new ShortcutRow("ウィンドウを前面に出す", hotkeys.ShowMainWindow, "表示 呼び出す メイン", isEditable: true),
                new ShortcutRow("フォーカスモードを開く", hotkeys.FocusMode, "集中 1件", isEditable: true),
            ], isAccent: true),
            new ShortcutCategory("タスクの操作",
            [
                new ShortcutRow("新しいタスク", "Ctrl+N", "追加 作る 作成"),
                new ShortcutRow("完了にする／戻す", "Space", "チェック 済み 未完了"),
                new ShortcutRow("ゴミ箱に入れる", "Delete", "削除 消す ごみばこ"),
                new ShortcutRow("複製する", "Ctrl+D", "コピー 写す"),
                new ShortcutRow("名前を書き換える", "F2|Enter", "編集 名前 変更 リネーム"),
                new ShortcutRow("取り消す", "Ctrl+Z", "元に戻す やり直し アンドゥ"),
                new ShortcutRow("サブタスクにする／戻す", "Tab|Shift+Tab", "階層 字下げ インデント 親子"),
            ]),
            new ShortcutCategory("表示を変える",
            [
                new ShortcutRow("コマンドパレット", "Ctrl+K", "探す 検索 実行 移動", isAccent: true),
                new ShortcutRow("検索する", "Ctrl+F", "探す 絞り込み"),
                new ShortcutRow("ビューを切り替える", "Ctrl+1〜6", "今日 予定 すべて 完了済み カレンダー 振り返り"),
                new ShortcutRow("サイドバーを隠す／出す", "Ctrl+B", "表示 非表示"),
                new ShortcutRow("詳細ペインを隠す／出す", "Ctrl+I", "表示 非表示 詳細"),
                new ShortcutRow("カレンダーの前後へ", "PageUp|PageDown", "前の月 次の月 前の週 次の週 移動"),
            ]),
        ];
        RightColumn =
        [
            new ShortcutCategory("選んだタスクを編集",
            [
                new ShortcutRow("優先度を付ける（詳細ペインで）", "Ctrl+1〜4", "重要 高 低 緊急"),
                new ShortcutRow("並び順を動かす", "Ctrl+↑↓", "並べ替え 上へ 下へ 移動"),
            ]),
            new ShortcutCategory("コマンドパレットの中で",
            [
                new ShortcutRow("開く／実行する", "Enter", "決定"),
                new ShortcutRow("閉じずに完了にする", "Space", "完了 済み チェック"),
                new ShortcutRow("入力した文字で作る", "Ctrl+Enter", "追加 作成 新しい タスク"),
                new ShortcutRow("プロジェクト／タグで絞り込む", "Tab", "絞り込み 続ける"),
            ]),
            // 使い捨てリスト（波3-N の実装が正本。統合担当から受け取った一覧をそのまま載せる）
            new ShortcutCategory("使い捨てリストの中で",
            [
                new ShortcutRow("下に空の行を足す（子があれば最初の子に）", "Enter", "使い捨て 追加 項目 新しい 行"),
                new ShortcutRow("チェックを切り替える", "Ctrl+Enter", "使い捨て 完了 済み チェック"),
                new ShortcutRow("字下げする／戻す（子ごと・3段まで）", "Tab|Shift+Tab", "使い捨て 階層 インデント 字下げ 親子"),
                new ShortcutRow("空の行を消す（子は1段上がる）", "Backspace", "使い捨て 削除 消す 行"),
                new ShortcutRow("行を移る（最後の行の↓で追加の欄へ）", "↑|↓", "使い捨て 移動 上 下 次 前"),
                new ShortcutRow("リストの外へ出る", "Ctrl+Tab", "使い捨て 抜ける 出る フォーカス"),
                new ShortcutRow("「項目を追加」の欄へ（メイン画面で）", "Ctrl+N", "使い捨て 追加 新しい 項目"),
            ], note: "メイン画面で出している間、Ctrl+Z はタスクの取り消しに使いません（トーストの「元に戻す」は効きます）"),
            new ShortcutCategory("そのほか",
            [
                new ShortcutRow("テンプレートから作る", "Ctrl+T", "テンプレート 展開"),
                new ShortcutRow("設定を開く", "Ctrl+,", "設定 オプション"),
                new ShortcutRow("戻る（検索を消す → 詳細を閉じる → しまう）", "Esc", "閉じる 戻る キャンセル 隠す"),
                new ShortcutRow("この一覧を開く", "?", "ヘルプ ショートカット キー 一覧", isAccent: true),
            ]),
        ];
    }

    public IReadOnlyList<ShortcutCategory> LeftColumn { get; }

    public IReadOnlyList<ShortcutCategory> RightColumn { get; }

    /// <summary>絞り込んだ結果が1行でもあるか（無ければ「当たるものはありません」）。</summary>
    [ObservableProperty]
    private bool _hasResults = true;

    /// <summary>動作名で絞り込む（空なら全部）。行が1つも残らないカテゴリは見出しごと隠す。</summary>
    public void ApplyFilter(string? text)
    {
        var terms = PaletteText.Terms(text);
        var any = false;
        foreach (var category in LeftColumn.Concat(RightColumn))
        {
            foreach (var row in category.Rows)
            {
                row.IsVisible = row.Matches(terms);
            }
            category.IsVisible = category.Rows.Any(r => r.IsVisible);
            any |= category.IsVisible;
        }
        HasResults = any;
    }

    /// <summary>「Ctrl+Shift+Space」をキーごとの枠と「+」に、「|」で区切った言い換えを「／」に分ける。範囲（「1〜6」）は1つの枠のまま。</summary>
    public static IReadOnlyList<KeyToken> ParseKeys(string keys, bool isAccent = false)
    {
        var tokens = new List<KeyToken>();
        foreach (var (alternative, index) in keys.Split('|').Select((a, i) => (a, i)))
        {
            if (index > 0)
            {
                tokens.Add(new KeyToken("／", KeyTokenKind.Or, isAccent));
            }
            var caps = alternative.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < caps.Length; i++)
            {
                if (i > 0)
                {
                    tokens.Add(new KeyToken("+", KeyTokenKind.Plus, isAccent));
                }
                tokens.Add(new KeyToken(caps[i], KeyTokenKind.Cap, isAccent));
            }
        }
        return tokens;
    }
}
