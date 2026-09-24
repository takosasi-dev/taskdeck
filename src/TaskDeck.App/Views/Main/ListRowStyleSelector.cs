using System.Windows;
using System.Windows.Controls;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 一覧の行の入れ物を選ぶ。タスク行だけホバーと選択の見た目を持ち、グループ見出しと入力行は素のまま。
/// </summary>
public sealed class ListRowStyleSelector : StyleSelector
{
    public Style? RowStyle { get; set; }

    public Style? PlainStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is ListItemViewModel { IsSelectable: true } ? RowStyle : PlainStyle;
}
