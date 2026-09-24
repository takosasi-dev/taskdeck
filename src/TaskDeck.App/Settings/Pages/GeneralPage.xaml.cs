using System.Windows.Controls;

namespace TaskDeck.App.Settings.Pages;

/// <summary>全般（S-03）。担当: 波1-D。</summary>
public partial class GeneralPage : UserControl
{
    public GeneralPage(GeneralPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
