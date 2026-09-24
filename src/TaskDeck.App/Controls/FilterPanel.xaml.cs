using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDeck.App.Input;
using TaskDeck.App.Services;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Controls;

/// <summary>適用された絞り込み条件。</summary>
public sealed class FilterAppliedEventArgs(TaskQuery query) : EventArgs
{
    public TaskQuery Query { get; } = query;
}

/// <summary>
/// 複合絞り込み（F-059）のパネル。一覧の見出しの「絞り込み」から Popup で開く。担当: 波2-G（BaseQuery は波2-E）。
/// 公開 API（変えない）: Query（開く前の条件）、BaseQuery（ビューの条件。「クリア」で戻す先）、Applied（新しい条件）、Cancelled。
/// 使い方: Popup（StaysOpen=False・AllowsTransparency=True）に置き、開く前に Query と BaseQuery を入れる。
/// Applied を受けたら一覧の条件にして閉じる。Cancelled（キャンセル・Esc）なら何もせず閉じる。
/// 変えるのは Query の絞り込みの8項目だけ（<see cref="FilterPanelModel"/>）。「この条件を保存」だけはパネルが設定に書く。
/// </summary>
public partial class FilterPanel : UserControl
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(
        nameof(Query), typeof(TaskQuery), typeof(FilterPanel), new PropertyMetadata(null, OnQueryChanged));

    /// <summary>いまのビューの条件（組み込みビューの条件か保存済みフィルタの条件）。「クリア」はここへ戻す。null なら TaskQuery の既定値。</summary>
    public static readonly DependencyProperty BaseQueryProperty = DependencyProperty.Register(
        nameof(BaseQuery), typeof(TaskQuery), typeof(FilterPanel), new PropertyMetadata(null));

    private readonly FilterPanelModel _model = new();

    /// <summary>期間のカレンダーを開いている間だけ変えた、ホストの Popup の StaysOpen の元の値。</summary>
    private bool? _hostStaysOpen;

    public FilterPanel()
    {
        InitializeComponent();
        // UserControl 自身の DataContext はホストのもの（Query="{Binding ...}" が効くように）。中身だけモデルにつなぐ
        Root.DataContext = _model;
        Loaded += OnLoaded;
    }

    public TaskQuery? Query
    {
        get => (TaskQuery?)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public TaskQuery? BaseQuery
    {
        get => (TaskQuery?)GetValue(BaseQueryProperty);
        set => SetValue(BaseQueryProperty, value);
    }

    public event EventHandler<FilterAppliedEventArgs>? Applied;

    public event EventHandler? Cancelled;

    protected void RaiseApplied(TaskQuery query) => Applied?.Invoke(this, new FilterAppliedEventArgs(query));

    private static void OnQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FilterPanel)d)._model.Load((TaskQuery?)e.NewValue ?? new TaskQuery());

    /// <summary>
    /// Popup は開くたびに Loaded が来る。そのたびに最新のプロジェクト・タグと、渡された条件で作り直す（前回の未適用の選択を残さない）。
    /// 開いたらすぐキーボードで操作できるよう、最初の項目（状態の先頭のチップ）にフォーカスを置く（Esc で閉じられる）。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SavedFilterName.Clear();
        SaveMessage.Visibility = Visibility.Collapsed;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusFirstItem);
        if (AppServices.IsReady)
        {
            // 読み終わるまで適用させない（途中で押すと、まだ無いタグの条件を落としてしまう）
            ApplyButton.IsEnabled = false;
            try
            {
                var projects = await AppServices.Get<IProjectRepository>().GetAllAsync(includeArchived: true);
                var tags = await AppServices.Get<ITagRepository>().GetAllAsync();
                _model.SetChoices(projects, tags, AppServices.Get<ThemeService>().IsDark);
            }
            finally
            {
                ApplyButton.IsEnabled = true;
            }
        }
        _model.Load(Query ?? new TaskQuery());
    }

    private void OnApply(object sender, RoutedEventArgs e) => RaiseApplied(_model.Apply(Query ?? new TaskQuery()));

    private void OnClear(object sender, RoutedEventArgs e) => _model.ResetTo(BaseQuery);

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>Esc で閉じる。ドロップダウンを開いているときの Esc はそちらが先に受けて閉じる（ここまで来ない）。</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>
    /// DatePicker のカレンダーは入れ子の Popup で、閉じるときにマウスのキャプチャが外れると、外側の Popup（StaysOpen=False）まで閉じてしまう。
    /// カレンダーを開いている間だけホストの Popup を StaysOpen=True にし、閉じたら元に戻す（戻すとホストがキャプチャを取り直す）。
    /// </summary>
    private void OnCalendarOpened(object sender, RoutedEventArgs e)
    {
        if (HostPopup() is { } host && _hostStaysOpen is null)
        {
            _hostStaysOpen = host.StaysOpen;
            host.StaysOpen = true;
        }
    }

    private void OnCalendarClosed(object sender, RoutedEventArgs e)
    {
        if (HostPopup() is { } host && _hostStaysOpen is { } staysOpen)
        {
            host.StaysOpen = staysOpen;
        }
        _hostStaysOpen = null;
    }

    /// <summary>
    /// 最初の項目（状態の先頭のチップ）にキーボードのフォーカスを置く。
    /// ホストの窓が前面にないとき（自動操作で開いたときなど）に Popup の中へフォーカスを移すと、Win32 のフォーカスが動いて
    /// マウスのキャプチャが外れ、Popup（StaysOpen=False）が開いた直後に閉じてしまうので、そのときは置かない。
    /// </summary>
    private void FocusFirstItem()
    {
        var hostWindow = HostPopup() is { } host ? Window.GetWindow(host) : null;
        if (hostWindow is null || hostWindow.IsActive)
        {
            FirstDescendant<ToggleButton>(StatusList)?.Focus();
        }
    }

    private static T? FirstDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FirstDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>このパネルを載せている Popup（論理ツリーをたどる。Popup に置かれていなければ null）。</summary>
    private Popup? HostPopup()
    {
        DependencyObject? node = this;
        while (node is not null and not Popup)
        {
            node = LogicalTreeHelper.GetParent(node);
        }
        return node as Popup;
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveCurrent();

    private void OnSavedFilterNameKeyDown(object sender, KeyEventArgs e)
    {
        if (ImeGuard.IsImeEnter(e, (TextBox)sender))
        {
            return;   // 変換確定の Enter では保存しない
        }
        if (e.Key == Key.Enter)
        {
            SaveCurrent();
            e.Handled = true;
        }
    }

    /// <summary>「この条件を保存」: 名前を付けて設定の SavedFilters に足す（サイドバーに出る）。</summary>
    private void SaveCurrent()
    {
        var result = _model.CreateSavedFilter(SavedFilterName.Text, Query ?? new TaskQuery());
        if (!result.Succeeded)
        {
            ShowSaveMessage(result.Error!, isError: true);
            return;
        }
        var filter = result.Value!;
        AppServices.Get<ISettingsStore>().Update(s => s.SavedFilters.Add(filter));
        SavedFilterName.Clear();
        ShowSaveMessage($"サイドバーに「{filter.Name}」を足しました。", isError: false);
    }

    private void ShowSaveMessage(string message, bool isError)
    {
        SaveMessage.Text = message;
        SaveMessage.SetResourceReference(TextBlock.ForegroundProperty, isError ? "StateOverdueBrush" : "TextSecondaryBrush");
        SaveMessage.Visibility = Visibility.Visible;
    }
}
