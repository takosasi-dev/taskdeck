using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace TaskDeck.App.Views.Palette;

/// <summary>
/// パレットの一致部分を アクセント色＋SemiBold で描く（UI 設計書 11.4。一覧の検索の黄色地とは表現を変える）。
/// TextBlock に Pieces（<see cref="TextPiece"/> の並び）を付けて使う。色は DynamicResource なのでテーマを切り替えても追従する。
/// </summary>
public static class PaletteHighlight
{
    public static readonly DependencyProperty PiecesProperty = DependencyProperty.RegisterAttached(
        "Pieces", typeof(IReadOnlyList<TextPiece>), typeof(PaletteHighlight), new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<TextPiece>? GetPieces(DependencyObject element) => (IReadOnlyList<TextPiece>?)element.GetValue(PiecesProperty);

    public static void SetPieces(DependencyObject element, IReadOnlyList<TextPiece>? value) => element.SetValue(PiecesProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }
        var pieces = GetPieces(block) ?? [];
        if (!pieces.Any(p => p.IsMatch))
        {
            // 強調が無いときは Text に入れる（Inlines より描画が軽い）
            block.Text = string.Concat(pieces.Select(p => p.Text));
            return;
        }
        block.Inlines.Clear();
        foreach (var piece in pieces)
        {
            var run = new Run(piece.Text);
            if (piece.IsMatch)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentDefaultBrush");
            }
            block.Inlines.Add(run);
        }
    }
}
