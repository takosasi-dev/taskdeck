using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskDeck.App.Input;
using TaskDeck.App.Services;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace TaskDeck.App.Views.Startup;

/// <summary>
/// 初回起動の3画面（S-15、F-181〜186）。ShellService.StartUp が出し、閉じたらメイン画面を出す（DI の Transient。閉じたら終わり）。
/// 閉じ方（最後まで・スキップ・×・Esc）によらず、閉じたらキーの横取りをやめ、終えた記録を書く。中身は <see cref="FirstRunViewModel"/>。
/// ③の入力欄は TextBox なので一部の文字だけ色を変えられない。読み取った日付の語は、入力欄の文字の位置を取って
/// 同じ書体の文字をアクセント色で上に重ねる（地も入力欄と同じ色で塗り、下の黒い文字を隠す。カーソルは上の層に出るので隠れない）。
/// </summary>
public partial class FirstRunWindow : FluentWindow
{
    private readonly FirstRunViewModel _viewModel;
    private readonly ShellService _shell;

    public FirstRunWindow(FirstRunService firstRun, FirstRunViewModel viewModel, ShellService shell)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _shell = shell;
        DataContext = viewModel;
        viewModel.GoTo(firstRun.DevStartStep ?? FirstRunStep.Welcome);
        viewModel.PropertyChanged += OnViewModelChanged;

        ImeGuard.AddCompositionCompletedHandler(InputBox, (_, _) => _viewModel.Refresh());
        InputBox.SelectionChanged += (_, _) => ScheduleHighlight();
        InputBox.SizeChanged += (_, _) => ScheduleHighlight();
        InputBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleHighlight()));

        Loaded += (_, _) => FocusStep();
        Closed += async (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Detach();
            await firstRun.MarkCompletedAsync();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FirstRunViewModel.Step):
                FocusStep();
                break;
            case nameof(FirstRunViewModel.DateTokens):
                ScheduleHighlight();
                break;
        }
    }

    /// <summary>画面ごとの最初のフォーカス。確認用の起動（前面にしない）では取らない。②は置かない（押すのはどのアプリからでも効くキー）。</summary>
    private void FocusStep()
    {
        if (!ShowActivated)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_viewModel.IsWelcome)
            {
                BeginButton.Focus();
            }
            else if (_viewModel.IsParse)
            {
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        });
    }

    private void OnBeginClick(object sender, RoutedEventArgs e) => _viewModel.Begin();

    private void OnNextClick(object sender, RoutedEventArgs e) => _viewModel.Next();

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>「キーが効かない・別のキーにしたい」: 設定のショートカットへ（キーを変えたら②は新しいキーで待つ）。</summary>
    private void OnKeyHelpClick(object sender, RoutedEventArgs e) => _shell.OpenSettings("shortcuts");

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Esc はスキップと同じ。変換中の Esc は IME が変換をやめるのに使う
        if (e.Key == Key.Escape && !ImeGuard.IsComposing(InputBox))
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // IME の変換中は読み取らない。確定したら CompositionCompleted で読む（CLAUDE.md 1.7）。
        // その間は文字の位置がずれるので、前に塗った色は消しておく
        if (ImeGuard.IsComposing(InputBox))
        {
            HighlightLayer.Children.Clear();
            return;
        }
        _viewModel.Refresh();
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (ImeGuard.IsImeEnter(e, InputBox) || e.Key != Key.Enter)
        {
            return; // 変換確定の Enter では登録しない
        }
        e.Handled = true;
        await SubmitAsync();
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        if (await _viewModel.SubmitAsync(composing: ImeGuard.IsComposing(InputBox)))
        {
            Close();
        }
    }

    /// <summary>入力欄の配置が済んでから塗り直す（文字の位置は配置の後でないと取れない）。</summary>
    private void ScheduleHighlight() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateHighlight);

    private void UpdateHighlight()
    {
        HighlightLayer.Children.Clear();
        var text = InputBox.Text;
        if (!_viewModel.IsParse || ImeGuard.IsComposing(InputBox) || InputBox.SelectionLength > 0)
        {
            return; // 範囲を選んでいる間は、選択の色を隠さないように塗らない
        }
        foreach (var token in _viewModel.DateTokens)
        {
            if (token.Length == 0 || token.Start + token.Length > text.Length
                || !string.Equals(text.Substring(token.Start, token.Length), token.Text, StringComparison.Ordinal))
            {
                continue; // 読み取った後に入力が変わった（次の読み取りで塗り直す）
            }
            var left = InputBox.GetRectFromCharacterIndex(token.Start);
            if (left.IsEmpty)
            {
                continue;
            }
            var word = new TextBlock
            {
                Text = token.Text,
                FontFamily = InputBox.FontFamily,
                FontSize = InputBox.FontSize,
                Height = left.Height,
            };
            word.SetResourceReference(TextBlock.ForegroundProperty, "AccentDefaultBrush");
            word.SetResourceReference(TextBlock.BackgroundProperty, "SurfaceContentBrush");
            Canvas.SetLeft(word, left.Left);
            Canvas.SetTop(word, left.Top);
            HighlightLayer.Children.Add(word);
        }
    }
}
