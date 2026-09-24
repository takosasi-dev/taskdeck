using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using TaskDeck.App.ViewModels;

namespace TaskDeck.App.Views.Main;

/// <summary>取り消しトースト（S-08）。ホバー中は消える時間の計測を止める。出たらスクリーンリーダーに読ませる。</summary>
public partial class ToastHost : UserControl
{
    private ToastViewModel? _boundModel;

    public ToastHost()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ToastViewModel? ViewModel => DataContext as ToastViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundModel is not null)
        {
            _boundModel.PropertyChanged -= OnModelPropertyChanged;
        }
        _boundModel = ViewModel;
        if (_boundModel is not null)
        {
            _boundModel.PropertyChanged += OnModelPropertyChanged;
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToastViewModel.Message) or nameof(ToastViewModel.IsOpen) && _boundModel?.IsOpen == true)
        {
            // 表示の更新（Visibility の反映）を待ってから知らせる
            Dispatcher.BeginInvoke(() =>
                (UIElementAutomationPeer.FromElement(ToastRoot) ?? UIElementAutomationPeer.CreatePeerForElement(ToastRoot))
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
    }

    private void OnMouseEnterToast(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.IsHovered = true;
        }
    }

    private void OnMouseLeaveToast(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.IsHovered = false;
        }
    }
}
