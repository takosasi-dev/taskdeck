using System.Windows;
using System.Windows.Input;

namespace TaskDeck.App.Input;

/// <summary>
/// 日本語入力（IME）の変換中を追跡する（CLAUDE.md 1.7・設計書 5.4）。
///
/// 使い方:
/// <code>
/// &lt;TextBox input:ImeGuard.IsEnabled="True" PreviewKeyDown="OnKeyDown" /&gt;
///
/// void OnKeyDown(object s, KeyEventArgs e)
/// {
///     if (ImeGuard.IsImeEnter(e, (TextBox)s)) return;   // 変換確定の Enter では登録しない
///     if (e.Key == Key.Enter) { 登録; e.Handled = true; }
/// }
/// </code>
/// インクリメンタル検索やクイック入力のパースは、TextChanged で ImeGuard.IsComposing が true の間は走らせず、
/// CompositionCompleted（変換が確定した）で走らせる。
/// </summary>
public static class ImeGuard
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ImeGuard), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty IsComposingProperty = DependencyProperty.RegisterAttached(
        "IsComposing", typeof(bool), typeof(ImeGuard), new PropertyMetadata(false));

    /// <summary>変換が確定した（未確定の文字が無くなった）。TextBox から上へバブルする。</summary>
    public static readonly RoutedEvent CompositionCompletedEvent = EventManager.RegisterRoutedEvent(
        "CompositionCompleted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ImeGuard));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>IME で変換中（未確定の文字がある）か。</summary>
    public static bool IsComposing(DependencyObject element) => (bool)element.GetValue(IsComposingProperty);

    public static void AddCompositionCompletedHandler(DependencyObject element, RoutedEventHandler handler) =>
        (element as UIElement)?.AddHandler(CompositionCompletedEvent, handler);

    public static void RemoveCompositionCompletedHandler(DependencyObject element, RoutedEventHandler handler) =>
        (element as UIElement)?.RemoveHandler(CompositionCompletedEvent, handler);

    /// <summary>
    /// この KeyDown が IME の変換確定などに使われた Enter なら true（登録してはいけない）。
    /// IME が処理したキー（WPF では Key.ImeProcessed。仕様書の ProcessKey に当たる）と、変換中の Enter をすべて弾く。
    /// </summary>
    public static bool IsImeEnter(KeyEventArgs e, DependencyObject source)
    {
        if (e.Key == Key.ImeProcessed || e.ImeProcessedKey == Key.Enter)
        {
            return true;
        }
        return e.Key == Key.Enter && IsComposing(source);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }
        if ((bool)e.NewValue)
        {
            TextCompositionManager.AddPreviewTextInputStartHandler(element, OnStart);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(element, OnUpdate);
            TextCompositionManager.AddPreviewTextInputHandler(element, OnInput);
            element.LostKeyboardFocus += OnLostFocus;
        }
        else
        {
            TextCompositionManager.RemovePreviewTextInputStartHandler(element, OnStart);
            TextCompositionManager.RemovePreviewTextInputUpdateHandler(element, OnUpdate);
            TextCompositionManager.RemovePreviewTextInputHandler(element, OnInput);
            element.LostKeyboardFocus -= OnLostFocus;
        }
    }

    private static void OnStart(object sender, TextCompositionEventArgs e)
    {
        // IME の変換開始（ふつうのキー入力でも一瞬だけ立つが、すぐ OnInput で下りる）
        if (!string.IsNullOrEmpty(e.TextComposition.CompositionText))
        {
            ((DependencyObject)sender).SetValue(IsComposingProperty, true);
        }
    }

    private static void OnUpdate(object sender, TextCompositionEventArgs e)
    {
        var d = (DependencyObject)sender;
        var composing = !string.IsNullOrEmpty(e.TextComposition.CompositionText);
        var was = IsComposing(d);
        d.SetValue(IsComposingProperty, composing);
        if (was && !composing)
        {
            RaiseCompleted(d);
        }
    }

    private static void OnInput(object sender, TextCompositionEventArgs e)
    {
        var d = (DependencyObject)sender;
        var was = IsComposing(d);
        d.SetValue(IsComposingProperty, false);
        if (was)
        {
            RaiseCompleted(d);
        }
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ((DependencyObject)sender).SetValue(IsComposingProperty, false);

    /// <summary>確定した文字が TextBox に入ってから知らせる（Input の優先度で後回しにする）。</summary>
    private static void RaiseCompleted(DependencyObject d)
    {
        if (d is UIElement element)
        {
            element.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                () => element.RaiseEvent(new RoutedEventArgs(CompositionCompletedEvent, element)));
        }
    }
}
