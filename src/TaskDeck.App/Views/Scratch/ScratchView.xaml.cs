using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDeck.App.Input;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Scratch;

/// <summary>
/// 使い捨てリストの中身（担当: 波3-N）。キー操作を受けて <see cref="ScratchListViewModel"/> を呼び、フォーカスを行に運ぶ。
/// フォーカスを動かすのは、この窓で打っている（窓が前面にある）ときだけ（確認用の起動で前面を奪わないため）。
/// </summary>
public partial class ScratchView : UserControl
{
    public ScratchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ScratchListViewModel? ViewModel => DataContext as ScratchListViewModel;

    /// <summary>「＋ 項目を追加…」にフォーカスを入れる（Ctrl+N・小窓を開いたとき）。窓が前面に無ければ何もしない。</summary>
    public void FocusAddBox()
    {
        if (Window.GetWindow(this) is { IsActive: true })
        {
            FocusRow(null);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ScratchListViewModel old)
        {
            old.ConfirmDiscard = null;
        }
        if (e.NewValue is ScratchListViewModel vm)
        {
            vm.ConfirmDiscard = ConfirmDiscard;
            // 項目のうしろに入力行をつなぐ（入力行も一覧と一緒にスクロールし、最後の項目のすぐ下に出る）
            RowList.ItemsSource = new CompositeCollection
            {
                new CollectionContainer { Collection = vm.Items },
                ScratchAddRow.Instance,
            };
            // 作ったばかりの空のリストは、すぐ打てるように入力欄へ
            if (vm.Items.Count == 0)
            {
                FocusAddBox();
            }
        }
        else
        {
            RowList.ItemsSource = null;
        }
    }

    /// <summary>未チェックが残っているのに捨てるときの確認（取り消せないので OS の確認ダイアログで聞く）。</summary>
    private bool ConfirmDiscard(ConfirmRequest request)
    {
        var owner = Window.GetWindow(this);
        var answer = owner is null
            ? MessageBox.Show(request.Message, request.Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(owner, request.Message, request.Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return answer == MessageBoxResult.OK;
    }

    /// <summary>
    /// 行の欄のキー: Enter で下に足す・Ctrl+Enter でチェック・Tab / Shift+Tab で字下げ・空の行で Backspace で消す・↑↓で行を移る。
    /// IME の変換中（変換確定の Enter を含む）は何もしない。
    /// </summary>
    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ScratchItemViewModel item || ViewModel is not { } vm)
        {
            return;
        }
        if (e.Key == Key.ImeProcessed || ImeGuard.IsImeEnter(e, box) || ImeGuard.IsComposing(box))
        {
            return;
        }
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when modifiers == ModifierKeys.Control:
                vm.Toggle(item);
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                FocusRow(vm.InsertBelow(item));
                break;
            case Key.Tab when modifiers == ModifierKeys.None:
                vm.Indent(item);
                break;
            case Key.Tab when modifiers == ModifierKeys.Shift:
                vm.Outdent(item);
                break;
            case Key.Back when box.Text.Length == 0 && modifiers == ModifierKeys.None:
                FocusRow(vm.Remove(item));
                break;
            case Key.Up when modifiers == ModifierKeys.None:
                var index = vm.Items.IndexOf(item);
                if (index > 0)
                {
                    FocusRow(vm.Items[index - 1]);
                }
                break;
            case Key.Down when modifiers == ModifierKeys.None:
                var next = vm.Items.IndexOf(item) + 1;
                FocusRow(next < vm.Items.Count ? vm.Items[next] : null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>「＋ 項目を追加…」のキー: Enter で末尾に足す（欄はそのまま）・↑で最後の行へ。</summary>
    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || ViewModel is not { } vm)
        {
            return;
        }
        if (e.Key == Key.ImeProcessed || ImeGuard.IsImeEnter(e, box) || ImeGuard.IsComposing(box))
        {
            return;
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (vm.AddFromBoxCommand.CanExecute(null))
            {
                vm.AddFromBoxCommand.Execute(null);
                RowList.ScrollIntoView(ScratchAddRow.Instance);
            }
        }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None && vm.Items.Count > 0)
        {
            e.Handled = true;
            FocusRow(vm.Items[^1]);
        }
    }

    /// <summary>行の欄（null なら「＋ 項目を追加…」）にフォーカスを移し、カーソルを末尾に置く。行ができるのを待ってから入れる。</summary>
    private void FocusRow(ScratchItemViewModel? item)
    {
        object target = item is null ? ScratchAddRow.Instance : item;
        RowList.ScrollIntoView(target);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (RowList.ItemContainerGenerator.ContainerFromItem(target) is DependencyObject container
                && FindDescendant<TextBox>(container) is { } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
        });
    }

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
