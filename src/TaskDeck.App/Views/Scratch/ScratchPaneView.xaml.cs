using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.Input;

namespace TaskDeck.App.Views.Scratch;

/// <summary>メイン画面の中央の使い捨てリスト（担当: 波3-N）。名前の欄の確定だけをここで受ける。</summary>
public partial class ScratchPaneView : UserControl
{
    public ScratchPaneView() => InitializeComponent();

    /// <summary>Ctrl+N: 「＋ 項目を追加…」へ。</summary>
    public void FocusAddBox() => ListContent.FocusAddBox();

    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as ScratchListViewModel)?.CommitName();

    /// <summary>名前の欄で Enter（変換確定の Enter は除く）: 名前を確定して項目の入力へ移る。</summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key != Key.Enter || ImeGuard.IsImeEnter(e, box))
        {
            return;
        }
        e.Handled = true;
        (box.DataContext as ScratchListViewModel)?.CommitName();
        ListContent.FocusAddBox();
    }
}
