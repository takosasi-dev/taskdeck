using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.Input;
using TaskDeck.App.Services;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Controls.Pickers;

/// <summary>
/// プロジェクトピッカー（S-09、UI 設計書 12.3）。担当: 波1-D。
/// 公開 API（変えない）: ProjectId（現在値）、Picked、Cancelled。「新しく作る」はパネルがプロジェクトを作ってから Picked を出す。
/// キーボード: 開くと絞り込み欄にフォーカス（前回の文字は消す）。↓ で一覧へ、一覧の先頭で ↑ なら絞り込み欄へ戻る。
/// 絞り込み欄の Enter は先頭の候補（候補が無ければその名前で作成）、一覧で Enter / Space、Esc で取り消し。
/// </summary>
public partial class ProjectPickerPanel : UserControl
{
    public static readonly DependencyProperty ProjectIdProperty = DependencyProperty.Register(
        nameof(ProjectId), typeof(Guid?), typeof(ProjectPickerPanel), new PropertyMetadata(null));

    private readonly ObservableCollection<Project> _shown = [];
    private IReadOnlyList<Project> _all = [];

    public ProjectPickerPanel()
    {
        InitializeComponent();
        ProjectList.ItemsSource = _shown;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
        // Popup は開くたびに中身を読み込み直す（Loaded が毎回来る）。一覧はそのたびに DB から取り直す（色もテーマに合わせ直る）
        Loaded += async (_, _) =>
        {
            FilterBox.Clear();
            FilterBox.Focus();
            if (AppServices.IsReady)
            {
                var theme = AppServices.Get<ThemeService>();
                theme.ThemeChanged -= OnThemeChanged; // Loaded が続けて来ても二重に購読しない
                theme.ThemeChanged += OnThemeChanged;
            }
            await ReloadAsync();
        };
        Unloaded += (_, _) =>
        {
            if (AppServices.IsReady)
            {
                AppServices.Get<ThemeService>().ThemeChanged -= OnThemeChanged;
            }
        };
    }

    /// <summary>色の点はコンバータがその時のテーマで作るので、開いている間にテーマが変わったら並べ直す。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyFilter();
        ProjectList.SelectedItem = _shown.FirstOrDefault(p => p.Id == ProjectId);
    }

    public Guid? ProjectId
    {
        get => (Guid?)GetValue(ProjectIdProperty);
        set => SetValue(ProjectIdProperty, value);
    }

    public event EventHandler<ProjectPickedEventArgs>? Picked;

    public event EventHandler? Cancelled;

    private string FilterText => FilterBox.Text.Trim();

    private async Task ReloadAsync()
    {
        if (!AppServices.IsReady)
        {
            return; // デザイナ表示
        }
        _all = await AppServices.Get<IProjectRepository>().GetAllAsync();
        ApplyFilter();
        ProjectList.SelectedItem = _shown.FirstOrDefault(p => p.Id == ProjectId);
    }

    private void ApplyFilter()
    {
        var text = TextNormalizer.ForSearch(FilterText);
        _shown.Clear();
        foreach (var project in _all)
        {
            if (text.Length == 0 || TextNormalizer.ForSearch(project.Name).Contains(text, StringComparison.Ordinal))
            {
                _shown.Add(project);
            }
        }
        CreateLabel.Text = FilterText.Length == 0 ? "新しく作る" : $"「{FilterText}」を作る";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        // IME の変換中は走らせない（CLAUDE.md 1.7）。確定したら OnCompositionCompleted で走る
        if (ImeGuard.IsComposing(FilterBox))
        {
            return;
        }
        ApplyFilter();
    }

    private void OnCompositionCompleted(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (ImeGuard.IsImeEnter(e, FilterBox))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down when _shown.Count > 0:
                ProjectList.SelectedItem = _shown[0];
                ProjectList.Focus();
                (ProjectList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when _shown.Count > 0:
                Pick(_shown[0].Id);
                e.Handled = true;
                break;
            case Key.Enter when FilterText.Length > 0:
                OnCreate(this, e);
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && ProjectList.SelectedIndex <= 0)
        {
            FilterBox.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Enter or Key.Space) || ProjectList.SelectedItem is not Project project)
        {
            return;
        }
        Pick(project.Id);
        e.Handled = true;
    }

    private void OnListClicked(object sender, MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is Project project)
        {
            Pick(project.Id);
            e.Handled = true;
        }
    }

    private void OnNone(object sender, RoutedEventArgs e) => Pick(null);

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = FilterText;
        if (name.Length == 0 || !AppServices.IsReady)
        {
            FilterBox.Focus();
            return;
        }
        // 「新しく作る」だけはパネルが書き込む（INTERFACES 5.3）
        var project = await AppServices.Get<IProjectRepository>().GetOrCreateAsync(name);
        Pick(project.Id);
    }

    private void Pick(Guid? id) => Picked?.Invoke(this, new ProjectPickedEventArgs(id));
}
