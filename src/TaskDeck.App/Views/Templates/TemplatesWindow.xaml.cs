using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDeck.App.Controls.Pickers;
using TaskDeck.App.Input;
using TaskDeck.App.Services;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Views.Templates;

/// <summary>
/// テンプレート（S-11、Ctrl+T）。ShellService.OpenTemplates から開く。担当: 波2-F。
/// ここでは画面の出し入れ（ポップアップ・フォーカス・キー）だけを持ち、中身は <see cref="TemplatesViewModel"/>。
/// Esc は段階的に: 開いているポップアップ → 削除の確認・編集・「タスクから作る」 → 窓を閉じる。
/// </summary>
public partial class TemplatesWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TemplatesViewModel _viewModel;
    private TemplateEditorViewModel? _editor;
    private Button? _popupOwner;

    public TemplatesWindow(TemplatesViewModel viewModel, DataChangeHub hub, ThemeService theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelChanged;
        KeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
        hub.Changed += OnDataChanged;
        theme.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            hub.Changed -= OnDataChanged;
            theme.ThemeChanged -= OnThemeChanged;
        };

        DatePicker.Picked += OnDatePicked;
        DatePicker.Cancelled += (_, _) => ClosePopup(DatePopup);
        ProjectPicker.Picked += OnProjectPicked;
        ProjectPicker.Cancelled += (_, _) => ClosePopup(ProjectPopup);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
        await Dispatcher.InvokeAsync(FocusSelectedTemplate, DispatcherPriority.Loaded);
    }

    /// <summary>ほかの画面でテンプレートが変わった（UI スレッド以外から来ることがあるので戻す）。一覧だけ読み直す。</summary>
    private void OnDataChanged(object? sender, DataChangedEventArgs e)
    {
        if (e.Kinds.HasFlag(DataChangeKind.Templates))
        {
            _ = Dispatcher.InvokeAsync(_viewModel.RefreshListAsync);
        }
    }

    /// <summary>テーマが変わった。テンプレートの色はコンバータがその時のテーマで作るので、色を使う表示を作り直す。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _viewModel.RefreshThemeColors();
        ColorChoiceList.Items.Refresh();
    }

    /// <summary>Esc（ポップアップやピッカーが自分で受けた Esc はここまで来ない）。</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        e.Handled = true;
        foreach (var popup in new[] { DatePopup, ProjectPopup, TagsPopup })
        {
            if (popup.IsOpen)
            {
                ClosePopup(popup);
                return;
            }
        }
        if (!_viewModel.GoBack())
        {
            Close();
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusSelectedTemplate);
    }

    private void FocusSelectedTemplate()
    {
        if (_viewModel.Selected is { } selected && TemplateList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
        {
            item.Focus();
            return;
        }
        TemplateList.Focus();
    }

    // ---- 探す（IME の変換中は絞り込まない） ----

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ImeGuard.IsComposing(SearchBox))
        {
            SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    private void OnSearchCompositionCompleted(object sender, RoutedEventArgs e) =>
        SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    // ---- ポップアップ（日付・プロジェクト・タグ） ----

    private void OnAnchorClick(object sender, RoutedEventArgs e)
    {
        DatePicker.Date = _viewModel.AnchorDate;
        DatePicker.Time = null;
        Open(DatePopup, (Button)sender);
    }

    private void OnFromTasksAnchorClick(object sender, RoutedEventArgs e)
    {
        DatePicker.Date = _viewModel.FromTasksAnchor;
        DatePicker.Time = null;
        Open(DatePopup, (Button)sender);
    }

    private void OnDatePicked(object? sender, DatePickedEventArgs e)
    {
        ClosePopup(DatePopup);
        if (e.Date is not { } date)
        {
            return;   // 「期限を消す」は基準日には当てはまらないので、何も変えない
        }
        if (_viewModel.Mode == TemplatesMode.FromTasks)
        {
            _viewModel.FromTasksAnchor = date;
        }
        else
        {
            _viewModel.AnchorDate = date;
        }
    }

    private void OnProjectClick(object sender, RoutedEventArgs e)
    {
        ProjectPicker.ProjectId = _viewModel.ProjectId;
        Open(ProjectPopup, (Button)sender);
    }

    private void OnEditorProjectClick(object sender, RoutedEventArgs e)
    {
        ProjectPicker.ProjectId = _editor?.DefaultProjectId;
        Open(ProjectPopup, (Button)sender);
    }

    private async void OnProjectPicked(object? sender, ProjectPickedEventArgs e)
    {
        ClosePopup(ProjectPopup);
        await _viewModel.PickProjectAsync(e.ProjectId);
    }

    private void OnTagsClick(object sender, RoutedEventArgs e) =>
        OpenTags(_viewModel.ExpansionTags, "付けるタグ", canReset: true, "選ぶと、項目ごとのタグの代わりに、ここで選んだタグだけを付けます。", sender);

    private void OnEditorTagsClick(object sender, RoutedEventArgs e)
    {
        if (_editor is not null)
        {
            OpenTags(_editor.DefaultTags, "既定のタグ（展開のたびに付く）", canReset: false, null, sender);
        }
    }

    private void OnItemTagsClick(object sender, RoutedEventArgs e)
    {
        if (_editor is not null)
        {
            OpenTags(_editor.ItemTags, "この項目のタグ", canReset: false, null, sender);
        }
    }

    private void OpenTags(TagSelection selection, string header, bool canReset, string? note, object sender)
    {
        TagsPanel.DataContext = selection;
        TagsHeader.Text = header;
        ResetTagsButton.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
        TagsNote.Text = note ?? "";
        Open(TagsPopup, (Button)sender);
        // タグの一覧はこの画面で書いた中身なので、キーボードだけでも選べるよう最初の欄へ送る（ピッカーは自分で取る）
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TagsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }

    private void OnResetTags(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetTagsCommand.Execute(null);
        ClosePopup(TagsPopup);
    }

    /// <summary>開く。ピッカーは開いたとき（Loaded）に自分でフォーカスを取るので、ここでは横取りしない（INTERFACES 5.3）。</summary>
    private void Open(Popup popup, Button owner)
    {
        _popupOwner = owner;
        popup.PlacementTarget = owner;
        popup.IsOpen = true;
    }

    /// <summary>選んだ・Esc で閉じたときは、開いたボタンへフォーカスを戻す（外をクリックして閉じたときは戻さない）。</summary>
    private void ClosePopup(Popup popup)
    {
        popup.IsOpen = false;
        _popupOwner?.Focus();
    }

    // ---- 編集（選んだ項目のタイトル欄へフォーカスを運ぶ） ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TemplatesViewModel.Editor))
        {
            return;
        }
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
        }
        _editor = _viewModel.Editor;
        if (_editor is not { } editor)
        {
            return;
        }
        editor.PropertyChanged += OnEditorChanged;
        // 新規は名前から、編集は最初の項目から打ち始められるように
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (editor.IsNew)
            {
                TemplateNameBox.Focus();
            }
            else
            {
                FocusEditorItem(editor.SelectedItem);
            }
        });
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TemplateEditorViewModel.SelectedItem))
        {
            FocusEditorItem(_editor?.SelectedItem);
        }
    }

    private void FocusEditorItem(EditorItem? item)
    {
        if (item is null)
        {
            return;
        }
        EditorItemList.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (EditorItemList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container
                && FindDescendant<TextBox>(container) is { IsKeyboardFocusWithin: false } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
        });
    }

    private void OnItemTitleFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EditorItem item } && _editor is { } editor && editor.SelectedItem != item)
        {
            editor.SelectedItem = item;
        }
    }

    /// <summary>
    /// タイトル欄のキー: Enter で次の項目、↑↓で行を移る、Alt+↑↓で並べ替え、Alt+→←で字下げ・字上げ。
    /// 変換確定の Enter では何もしない（ImeGuard）。
    /// </summary>
    private void OnItemTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _editor is not { } editor || ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        ICommand? command = (key, alt) switch
        {
            (Key.Enter, false) => editor.AddItemCommand,
            (Key.Up, true) => editor.MoveUpCommand,
            (Key.Down, true) => editor.MoveDownCommand,
            (Key.Right, true) => editor.IndentCommand,
            (Key.Left, true) => editor.OutdentCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
            e.Handled = true;
            FocusEditorItem(editor.SelectedItem);   // 並べ替えで行が作り直されても、同じ項目で打ち続けられるように
            return;
        }
        if (key is Key.Up or Key.Down && !alt && box.DataContext is EditorItem item)
        {
            var index = editor.Items.IndexOf(item) + (key == Key.Down ? 1 : -1);
            if (index >= 0 && index < editor.Items.Count)
            {
                editor.SelectedItem = editor.Items[index];
            }
            e.Handled = true;
        }
    }

    // ---- 確認用（開発時だけ出るボタン） ----

    private void OnOpenRecurrenceCheck(object sender, RoutedEventArgs e) =>
        new RecurrencePickerDevWindow { Owner = this }.Show();

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }
}
