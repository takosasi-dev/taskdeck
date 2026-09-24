using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TaskDeck.App.Behaviors;

/// <summary>
/// 検索の一致部分に地色を敷く（UI 設計書 15.2）。TextBlock に Text と Terms を付けて使う。
/// ponytail: 一致は元の文字列に対する大文字小文字無視の単純探索。全角と半角がまたがる一致は強調しない
/// （検索自体は NFKC で当たるので行は出る）。必要になったら正規化の位置対応表を持つ方式に上げる。
/// </summary>
public static class HighlightText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HighlightText), new PropertyMetadata(null, OnChanged));

    /// <summary>空白区切りの強調語。空なら普通の文字列として出す。</summary>
    public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
        "Terms", typeof(string), typeof(HighlightText), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetTerms(DependencyObject element) => (string?)element.GetValue(TermsProperty);

    public static void SetTerms(DependencyObject element, string? value) => element.SetValue(TermsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }
        var text = GetText(block) ?? "";
        var terms = (GetTerms(block) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || text.Length == 0)
        {
            // 強調が無いときは Text に入れる（Inlines を使うと TextBlock が遅い描画経路になり、一覧の行が重くなる）
            block.Text = text;
            return;
        }
        block.Inlines.Clear();

        var highlight = block.TryFindResource("SearchHighlightBrush") as Brush;
        var index = 0;
        while (index < text.Length)
        {
            var bestStart = -1;
            var bestLength = 0;
            foreach (var term in terms)
            {
                var at = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && (bestStart < 0 || at < bestStart))
                {
                    bestStart = at;
                    bestLength = term.Length;
                }
            }
            if (bestStart < 0)
            {
                block.Inlines.Add(new Run(text[index..]));
                return;
            }
            if (bestStart > index)
            {
                block.Inlines.Add(new Run(text[index..bestStart]));
            }
            block.Inlines.Add(new Run(text.Substring(bestStart, bestLength)) { Background = highlight });
            index = bestStart + bestLength;
        }
    }
}

/// <summary>
/// その場編集の入力欄。表示された瞬間にフォーカスを入れて全選択する（F2・Ctrl+N・名前の変更）。
/// </summary>
public static class AutoFocus
{
    public static readonly DependencyProperty WhenVisibleProperty = DependencyProperty.RegisterAttached(
        "WhenVisible", typeof(bool), typeof(AutoFocus), new PropertyMetadata(false, OnChanged));

    public static bool GetWhenVisible(DependencyObject element) => (bool)element.GetValue(WhenVisibleProperty);

    public static void SetWhenVisible(DependencyObject element, bool value) => element.SetValue(WhenVisibleProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }
        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += OnVisibleChanged;
        }
        else
        {
            element.IsVisibleChanged -= OnVisibleChanged;
        }
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not FrameworkElement element)
        {
            return;
        }
        // テンプレートの適用が終わってからフォーカスを入れる
        element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            element.Focus();
            if (element is TextBox box)
            {
                box.SelectAll();
            }
        });
    }
}
