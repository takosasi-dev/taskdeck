using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TaskDeck.App.Controls.Pickers;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Templates;

/// <summary>
/// 繰り返しピッカーを確かめる小窓（開発時だけ。テンプレート画面の確認用ボタンから開く）。
/// ホスト（詳細ペイン）と同じく、開く前の値を DP に入れ、Picked / Cancelled を受けて結果を出す。
/// 「ポップアップで開く」は詳細ペインと同じ置き方（Popup の直下にパネル、枠はパネルが持つ）。
/// </summary>
public partial class RecurrencePickerDevWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IClock _clock = AppServices.Get<IClock>();

    public RecurrencePickerDevWindow()
    {
        InitializeComponent();
        Picker.Picked += (_, e) => ShowResult(e);
        Picker.Cancelled += (_, _) => ResultText.Text = "Cancelled（Esc）";
        PopupPicker.Picked += (_, e) =>
        {
            PickerPopup.IsOpen = false;
            ShowResult(e);
        };
        PopupPicker.Cancelled += (_, _) =>
        {
            PickerPopup.IsOpen = false;
            ResultText.Text = "Cancelled（Esc）";
        };
        Apply("none");
    }

    private void OnSample(object sender, RoutedEventArgs e) => Apply((string)((Button)sender).Tag);

    /// <summary>右のパネルと同じ値でポップアップを開く。</summary>
    private void OnOpenPopup(object sender, RoutedEventArgs e)
    {
        PopupPicker.Recurrence = Picker.Recurrence;
        PopupPicker.BaseDueAt = Picker.BaseDueAt;
        PopupPicker.DueHasTime = Picker.DueHasTime;
        PickerPopup.IsOpen = true;
    }

    private void ShowResult(RecurrencePickedEventArgs e) =>
        ResultText.Text = e.Recurrence is { } r
            ? $"Picked: {r.RRule}（{RecurrenceText.Describe(r.RRule)}）/ 起点 {r.BaseKind} / 終了 {r.EndKind} {r.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{r.MaxOccurrences}"
            : "Picked: なし（繰り返しを解除）";

    private void Apply(string sample)
    {
        var today = _clock.LocalToday();
        (RecurrenceInput? Input, DateTime? Due, bool HasTime) value = sample switch
        {
            "weekly" => (new RecurrenceInput(RecurrencePresets.Weekly([DayOfWeek.Monday, DayOfWeek.Wednesday])), _clock.LocalDayStartUtc(today), false),
            "monthly" => (new RecurrenceInput(RecurrencePresets.MonthlyByWeekday(3, DayOfWeek.Monday), RecurrenceBaseKind.CompletedDate, RecurrenceEndKind.Count, MaxOccurrences: 5),
                _clock.LocalDayStartUtc(today), false),
            "custom" => (new RecurrenceInput(RecurrencePresets.Daily(3)), _clock.LocalToUtc(today.AddDays(1), new TimeOnly(15, 0)), true),
            "raw" => (new RecurrenceInput("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29"), _clock.LocalDayStartUtc(today), false),
            "until" => (new RecurrenceInput(RecurrencePresets.Daily(), EndKind: RecurrenceEndKind.UntilDate, EndDate: new DateOnly(today.Year, 12, 31)), null, false),
            _ => (null, _clock.LocalDayStartUtc(today), false),
        };
        Picker.Recurrence = value.Input;
        Picker.BaseDueAt = value.Due;
        Picker.DueHasTime = value.HasTime;
        ResultText.Text = "（まだ決めていません）";
    }
}
