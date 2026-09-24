using System.Collections;
using System.Windows.Controls;

namespace TaskDeck.App.Views.Main;

/// <summary>
/// 一覧の ListBox。複数選択（SelectionMode=Extended）の選びをまとめて入れ替えるためだけに、ListBox の SetSelectedItems を出す
/// （SelectedItems に1件ずつ足すと足すたびに SelectionChanged が来て、1万件の全選択で止まるため）。
/// 見た目は WPF-UI の ListBox のスタイルをそのまま使う（XAML で Style="{StaticResource {x:Type ListBox}}" を付ける）。
/// </summary>
public sealed class TaskListBox : ListBox
{
    /// <summary>選んでいる行を rows にする（1回の SelectionChanged で済む）。先頭が SelectedItem になる。</summary>
    public void SelectRows(IEnumerable rows) => SetSelectedItems(rows);
}
