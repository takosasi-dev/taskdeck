using System.Globalization;
using System.Windows.Input;
using TaskDeck.App.Controls.Pickers;
using TaskDeck.Core;
using TaskDeck.Core.Time;
using Wpf.Ui.Controls;

namespace TaskDeck.App.Settings;

/// <summary>
/// 入力補助ポップアップ3種を並べて確かめる窓（開発時のみ）。担当: 波1-D。
/// 実際の画面では Popup の中に入れて使う（INTERFACES 5.3）。ここは見た目とキー操作の確認用。
/// </summary>
public partial class DevPickerPreviewWindow : FluentWindow
{
    public DevPickerPreviewWindow()
    {
        InitializeComponent();
        // Esc はまずピッカーが受けて Cancelled を出す（Handled）。ピッカーの外で押したときだけ窓を閉じる
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        Loaded += (_, _) =>
        {
            // ホストが開く前に現在値を入れるのと同じことをする（今日の輪と選択中の丸の両方が見えるよう、期限は3日後）
            if (AppServices.IsReady)
            {
                DatePanel.Date = AppServices.Get<IClock>().LocalToday().AddDays(3);
            }
            PriorityPanel.Priority = Priority.High;
        };
    }

    private void OnDatePicked(object? sender, DatePickedEventArgs e) =>
        ResultText.Text = e.Date is null
            ? "日付: 期限を消す"
            : $"日付: {e.Date.Value.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)}"
                + (e.Time is null ? "（終日）" : $" {e.Time.Value.ToString("HH\\:mm", CultureInfo.InvariantCulture)}");

    private void OnPriorityPicked(object? sender, PriorityPickedEventArgs e) =>
        ResultText.Text = $"優先度: {e.Priority}";

    private void OnProjectPicked(object? sender, ProjectPickedEventArgs e) =>
        ResultText.Text = e.ProjectId is null ? "プロジェクト: なし" : $"プロジェクト: {e.ProjectId}";

    private void OnCancelled(object? sender, EventArgs e) => ResultText.Text = "Cancelled（Esc）";
}
