using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TaskDeck.App.Input;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 右の詳細ペイン（S-06）。担当: 波1-C。
/// ピッカー（1-D / 2-F が中身を作る）は INTERFACES 5.3 のとおり Popup に入れ、Picked で保存して閉じる。
/// </summary>
public partial class DetailPaneView : UserControl
{
    public DetailPaneView()
    {
        InitializeComponent();

        DatePicker.Picked += async (_, e) =>
        {
            DuePopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.ApplyDueAsync(e.Date, e.Time);
            }
        };
        DatePicker.Cancelled += (_, _) => DuePopup.IsOpen = false;

        PriorityPicker.Picked += async (_, e) =>
        {
            PriorityPopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.SetPriorityAsync(e.Priority);
            }
        };
        PriorityPicker.Cancelled += (_, _) => PriorityPopup.IsOpen = false;

        ProjectPicker.Picked += async (_, e) =>
        {
            ProjectPopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.SetProjectAsync(e.ProjectId);
            }
        };
        ProjectPicker.Cancelled += (_, _) => ProjectPopup.IsOpen = false;

        RecurrencePicker.Picked += async (_, e) =>
        {
            RecurrencePopup.IsOpen = false;
            if (ViewModel is { } vm)
            {
                await vm.SetRecurrenceAsync(e.Recurrence);
            }
        };
        RecurrencePicker.Cancelled += (_, _) => RecurrencePopup.IsOpen = false;

        ImeGuard.AddCompositionCompletedHandler(TagBox, (_, _) => ViewModel?.UpdateTagSuggestions());
    }

    private TaskDetailViewModel? ViewModel => DataContext as TaskDetailViewModel;

    private Popup[] AllPopups => [StatusPopup, DuePopup, PriorityPopup, ProjectPopup, RecurrencePopup, ReminderPopup, DurationPopup];

    /// <summary>「…」（その他の操作）が押された。メイン画面が一覧の行のメニューをこのボタンの下に開く。</summary>
    public event EventHandler<UIElement>? MoreActionsRequested;

    private void OnMoreActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement button)
        {
            MoreActionsRequested?.Invoke(this, button);
        }
    }

    /// <summary>開いているポップアップを閉じる（Esc）。閉じたものがあれば true。</summary>
    public bool CloseOpenPopups()
    {
        var closed = false;
        foreach (var popup in AllPopups)
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                closed = true;
            }
        }
        if (ViewModel is { IsTagSuggestionOpen: true } vm)
        {
            vm.IsTagSuggestionOpen = false;
            closed = true;
        }
        return closed;
    }

    private void Open(Popup popup, object sender)
    {
        CloseOpenPopups();
        popup.PlacementTarget = sender as UIElement;
        popup.IsOpen = true;
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is Popup popup)
        {
            e.Handled = true;
            popup.IsOpen = false;
        }
    }

    // ---- タイトルとメモ ----

    private async void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ViewModel is not { } vm)
        {
            return;
        }
        if (ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.CommitTitleAsync(box.Text);
            ShowTitle(box, vm);
            box.CaretIndex = box.Text.Length;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ShowTitle(box, vm);
        }
    }

    private async void OnTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && ViewModel is { } vm)
        {
            await vm.CommitTitleAsync(box.Text);
            if (!box.IsKeyboardFocused)
            {
                ShowTitle(box, vm);
            }
        }
    }

    /// <summary>保存された値を欄に戻す（SetCurrentValue なので Title へのバインディングは切れない）。</summary>
    private static void ShowTitle(TextBox box, TaskDetailViewModel vm) =>
        box.SetCurrentValue(TextBox.TextProperty, vm.Title);

    private async void OnNotesLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && ViewModel is { } vm)
        {
            await vm.CommitNotesAsync(box.Text);
        }
    }

    // ---- 各項目のポップアップ ----

    private void OnStatusClick(object sender, RoutedEventArgs e) => Open(StatusPopup, sender);

    private void OnDueClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            DatePicker.Date = vm.DueDate;
            DatePicker.Time = vm.DueTime;
        }
        Open(DuePopup, sender);
    }

    private void OnPriorityClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            PriorityPicker.Priority = vm.Priority;
        }
        Open(PriorityPopup, sender);
    }

    private void OnProjectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            ProjectPicker.ProjectId = vm.ProjectId;
        }
        Open(ProjectPopup, sender);
    }

    private void OnRecurrenceClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            RecurrencePicker.Recurrence = vm.Recurrence;
            RecurrencePicker.BaseDueAt = vm.BaseDueAt;
            RecurrencePicker.DueHasTime = vm.DueHasTime;
        }
        Open(RecurrencePopup, sender);
    }

    private void OnReminderClick(object sender, RoutedEventArgs e) => Open(ReminderPopup, sender);

    private void OnDurationClick(object sender, RoutedEventArgs e) => Open(DurationPopup, sender);

    private async void OnStatusChoiceClick(object sender, RoutedEventArgs e)
    {
        StatusPopup.IsOpen = false;
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is StatusOption option)
        {
            await vm.SetStatusCommand.ExecuteAsync(option);
        }
    }

    private async void OnReminderChoiceClick(object sender, RoutedEventArgs e)
    {
        ReminderPopup.IsOpen = false;
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is ChoiceOption option)
        {
            await vm.SetReminderCommand.ExecuteAsync(option);
        }
    }

    private async void OnDurationChoiceClick(object sender, RoutedEventArgs e)
    {
        DurationPopup.IsOpen = false;
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is ChoiceOption option)
        {
            await vm.SetDurationCommand.ExecuteAsync(option);
        }
    }

    // ---- サブタスク ----

    /// <summary>「サブタスクを追加」の欄にフォーカスを入れる（行の右クリックメニューの「サブタスクを追加」から）。</summary>
    public void FocusSubtaskInput() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (SubtaskBox.IsVisible)
            {
                SubtaskBox.BringIntoView();
                SubtaskBox.Focus();
            }
        });

    private async void OnAddSubtaskClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.AddSubtaskAsync();
            SubtaskBox.Focus();
        }
    }

    /// <summary>Enter で足して、欄に残る（続けて打てるように）。変換確定の Enter では足さない。Esc は書きかけを消す。</summary>
    private async void OnSubtaskKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ViewModel is not { } vm || ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.AddSubtaskAsync();
        }
        else if (e.Key == Key.Escape && box.Text.Length > 0)
        {
            e.Handled = true;
            vm.SubtaskInput = "";
        }
    }

    // ---- タグ ----

    private async void OnTagKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ViewModel is not { } vm)
        {
            return;
        }
        if (ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.AddTagAsync(box.Text);
        }
        else if (e.Key == Key.Escape && (vm.IsTagSuggestionOpen || box.Text.Length > 0))
        {
            e.Handled = true;
            vm.TagInput = "";
            vm.IsTagSuggestionOpen = false;
        }
    }

    private void OnTagTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsKeyboardFocused && !ImeGuard.IsComposing(box))
        {
            ViewModel?.UpdateTagSuggestions();
        }
    }

    private void OnTagGotFocus(object sender, KeyboardFocusChangedEventArgs e) => ViewModel?.UpdateTagSuggestions();

    private void OnTagLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.IsTagSuggestionOpen = false;
        }
    }

    private async void OnTagSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as FrameworkElement)?.DataContext is TagChipViewModel suggestion)
        {
            await vm.PickTagSuggestionAsync(suggestion);
        }
    }

    /// <summary>詳細ペイン内の Ctrl+1〜4 で優先度（低・中・高・緊急）。</summary>
    private async void OnPaneKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || ViewModel is not { } vm)
        {
            return;
        }
        var priority = e.Key switch
        {
            Key.D1 or Key.NumPad1 => Priority.Low,
            Key.D2 or Key.NumPad2 => Priority.Medium,
            Key.D3 or Key.NumPad3 => Priority.High,
            Key.D4 or Key.NumPad4 => Priority.Urgent,
            _ => (Priority?)null,
        };
        if (priority is { } value)
        {
            e.Handled = true;
            await vm.SetPriorityAsync(value);
        }
    }
}
