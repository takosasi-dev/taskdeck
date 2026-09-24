using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace TaskDeck.App.Views.Calendar;

/// <summary>
/// カレンダー（S-04）。メイン画面の中央に一覧の代わりに置く（Ctrl+5・ステータスバーの切替）。担当: 波3-I。
/// 公開 API（変えない）: SelectedTaskId（双方向。選ぶと詳細ペインに出る）。依存サービスは AppServices から取る。
/// 中身は CalendarViewModel（DataContext は Root に入れる）。ドラッグは CalendarView.Drag.cs。
/// </summary>
public partial class CalendarView : UserControl
{
    public static readonly DependencyProperty SelectedTaskIdProperty = DependencyProperty.Register(
        nameof(SelectedTaskId), typeof(Guid?), typeof(CalendarView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTaskIdChanged));

    /// <summary>週表示の1時間の高さ（UI 設計書 16。15分で 13px）。時間軸の Grid の高さ 1248 = 52×24 と合わせてある。</summary>
    public const double HourHeight = 52;

    /// <summary>時間軸の左の時刻（0:00 は上端で切れるので出さない）。</summary>
    public static IReadOnlyList<string> HourLabels { get; } =
        [.. Enumerable.Range(0, 24).Select(h => h == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{h}:00"))];

    private readonly CalendarViewModel? _viewModel;
    private readonly ILogger<CalendarView>? _logger;
    private readonly DispatcherTimer _minuteTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _scrollToNowPending = true;

    public CalendarView()
    {
        InitializeComponent();
        if (!AppServices.IsReady)
        {
            return; // デザイナ
        }
        _viewModel = AppServices.Get<CalendarViewModel>();
        _logger = AppServices.Get<ILogger<CalendarView>>();
        Root.DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Reloaded += OnReloaded;
        _minuteTimer.Tick += (_, _) => _viewModel.OnMinuteTick();
        IsVisibleChanged += OnIsVisibleChanged;
        TimeGrid.SizeChanged += (_, _) => PlaceNowLine();
        MonthGrid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height > 0) // 週表示の間（畳まれて 0）は変えない
            {
                _viewModel.SetMonthChipCapacity(ChipCapacity(e.NewSize.Height / 6));
            }
        };
        InitializeDrag();
    }

    /// <summary>
    /// 月表示の1セルに入るチップの数。セルの高さから上下の余白 8・日付の行 19・「他 N 件」18・下の枠 1 を除き、
    /// チップ1つ（高さ 18＋間 2）で割る（既定の 1280×800 の窓でちょうど3件）。
    /// </summary>
    private static int ChipCapacity(double cellHeight) => (int)Math.Floor((cellHeight - 46) / 20);

    public Guid? SelectedTaskId
    {
        get => (Guid?)GetValue(SelectedTaskIdProperty);
        set => SetValue(SelectedTaskIdProperty, value);
    }

    /// <summary>メイン画面から入った選択（一覧で選んだタスクなど）。カレンダーに出ていれば強調する。</summary>
    private static void OnSelectedTaskIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (((CalendarView)d)._viewModel is { } viewModel)
        {
            viewModel.SelectedTaskId = (Guid?)e.NewValue;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalendarViewModel.SelectedTaskId):
                // 双方向の結び付けを保ったまま、メイン画面へ返す
                SetCurrentValue(SelectedTaskIdProperty, _viewModel!.SelectedTaskId);
                break;
            case nameof(CalendarViewModel.NowMinute):
                PlaceNowLine();
                break;
            case nameof(CalendarViewModel.Mode):
                _scrollToNowPending = true;
                MorePopup.IsOpen = false;
                break;
        }
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        if (e.NewValue is not true)
        {
            _minuteTimer.Stop();
            _viewModel.Deactivate();
            MorePopup.IsOpen = false;
            EndDrag();
            return;
        }
        // 起動時はメイン画面の表示切替（Visibility の結び付け）が効く前に一瞬「見えている」になるので、一巡待ってから確かめる
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
        if (!IsVisible)
        {
            return;
        }
        _scrollToNowPending = true;
        _minuteTimer.Start();
        await _viewModel.ActivateAsync();
        ScrollToNowIfPending();
    }

    private void OnReloaded(object? sender, CalendarReloadedEventArgs e)
    {
        ScrollToNowIfPending();
        PlaceNowLine();
        // 描画まで含めた所要時間（1万件のデータで月の切替が引っかからないかを見る）
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            _logger?.LogDebug("カレンダーを描画しました（{Mode}、{Elapsed} ms）", _viewModel?.Mode, e.Watch.ElapsedMilliseconds));
    }

    /// <summary>週表示を開いたら、現在時刻の1時間前が上に来るようにスクロールする（UI 設計書 16）。</summary>
    private void ScrollToNowIfPending()
    {
        if (!_scrollToNowPending || _viewModel is not { IsWeek: true } viewModel || viewModel.WeekDays.Count == 0)
        {
            return;
        }
        _scrollToNowPending = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            TimeScroller.ScrollToVerticalOffset(Math.Max(0, (viewModel.NowMinute - 60) / 60 * HourHeight)));
    }

    private void PlaceNowLine()
    {
        if (_viewModel is null || TimeGrid.ActualHeight <= 0)
        {
            return;
        }
        var y = _viewModel.NowMinute / CalendarBuilder.MinutesPerDay * TimeGrid.ActualHeight;
        NowLine.Margin = new Thickness(0, y - (NowLine.Height / 2), 0, 0);
    }

    /// <summary>時間軸の縦スクロールバーの幅だけ、見出しと終日帯の右を空けて列をそろえる。</summary>
    private void OnTimeScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var margin = new Thickness(0, 0, Math.Max(0, TimeScroller.ActualWidth - TimeScroller.ViewportWidth), 0);
        if (WeekHeaderList.Margin != margin)
        {
            WeekHeaderList.Margin = margin;
            AllDayArea.Margin = margin;
        }
    }

    /// <summary>チップ・ブロックを押したら選び、「他 N 件」を押したらその日の一覧を出す。</summary>
    private void OnRootClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button button)
        {
            return;
        }
        switch (button.DataContext)
        {
            case CalendarEntry entry:
                _viewModel?.SelectCommand.Execute(entry);
                e.Handled = true;
                break;
            case CalendarCell { Day: { } day }:
                MoreRoot.DataContext = day;
                MorePopup.PlacementTarget = button;
                MorePopup.IsOpen = true;
                e.Handled = true;
                break;
        }
    }

    private void OnMoreEntryClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { DataContext: CalendarEntry entry })
        {
            _viewModel?.SelectCommand.Execute(entry);
            MorePopup.IsOpen = false;
            e.Handled = true;
        }
    }
}
