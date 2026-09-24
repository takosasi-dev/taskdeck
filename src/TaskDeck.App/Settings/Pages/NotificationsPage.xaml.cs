using System.Windows.Controls;

namespace TaskDeck.App.Settings.Pages;

/// <summary>通知（S-03、F-124）。担当: 波1-D（値の保存）、波3-H（一時停止）。</summary>
public partial class NotificationsPage : UserControl
{
    public NotificationsPage(NotificationsPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // トレイから止めた・止めた時間が過ぎた、を出すたびに反映する
        IsVisibleChanged += (_, _) => viewModel.RefreshPause();
    }
}
