using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TaskDeck.App.Services;

namespace TaskDeck.App.Views.Stats;

/// <summary>
/// 振り返り（S-13）。メイン画面の中央に一覧の代わりに置く（Ctrl+6・サイドバー）。担当: 波3-K。
/// 公開 API: なし（表示されたら自分で集計する）。依存サービスは AppServices から取る。
/// ここは画面の都合だけを持つ: 見えたら ViewModel に知らせる・テーマの明暗を渡す・幅に合わせて並びを変える・ファイルダイアログ。
/// </summary>
public partial class StatsView : UserControl
{
    /// <summary>これより狭いと、指標を2列に・推移の右の欄を下に回す。</summary>
    private const double NarrowWidth = 760;

    /// <summary>見えてから集計を始めるまでの待ち（起動直後の一瞬の表示では読まない）。</summary>
    private const int ActivationDelayMs = 150;

    private bool? _narrow;

    public StatsView()
    {
        InitializeComponent();
        if (!AppServices.IsReady)
        {
            return;   // デザイナ
        }
        // この UserControl の DataContext はメイン画面のもの（Visibility のバインドに使われる）なので、中身だけつなぐ
        Root.DataContext = AppServices.Get<StatsViewModel>();
        var theme = AppServices.Get<ThemeService>();
        ViewModel.SetDarkTheme(theme.IsDark);
        theme.ThemeChanged += (_, _) => ViewModel.SetDarkTheme(theme.IsDark);
        IsVisibleChanged += OnVisibleChanged;
        SizeChanged += (_, _) => ApplyLayout();
    }

    internal StatsViewModel ViewModel => (StatsViewModel)Root.DataContext;

    /// <summary>
    /// 本当に見えたときだけ集計する。起動の直後は、メイン画面の Visibility のバインドが付くまでの一瞬だけ IsVisible=true になるので、
    /// 少し待ってもまだ見えていたら読む（見えていない間の変更は、次に見えたときにまとめて読み直す）。
    /// </summary>
    // 例外は Dispatcher の未処理例外ハンドラ（App.xaml.cs）まで上げてログに残す
    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            await ViewModel.SetActiveAsync(false);
            return;
        }
        await Task.Delay(ActivationDelayMs);
        if (IsVisible)
        {
            await ViewModel.SetActiveAsync(true);
        }
    }

    /// <summary>幅に合わせて並べ替える（狭いと指標を2列×2段、内訳・曜日の欄を推移の下へ）。</summary>
    private void ApplyLayout()
    {
        var narrow = ActualWidth < NarrowWidth;
        if (_narrow == narrow)
        {
            return;
        }
        _narrow = narrow;

        KpiColumn0.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(260);
        KpiColumn2.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        KpiColumn3.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(HeroCard, 0, 0, new Thickness(0, 0, 12, 0));
        Place(OnTimeCard, 0, 1, narrow ? new Thickness(0) : new Thickness(0, 0, 12, 0));
        Place(StreakCard, narrow ? 1 : 0, narrow ? 0 : 2, narrow ? new Thickness(0, 12, 12, 0) : new Thickness(0, 0, 12, 0));
        Place(LeadCard, narrow ? 1 : 0, narrow ? 1 : 3, narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0));

        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(300);
        TrendCard.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        Place(SideCard, narrow ? 1 : 0, narrow ? 0 : 1, narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0));
    }

    private static void Place(FrameworkElement element, int row, int column, Thickness margin)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        element.Margin = margin;
    }

    /// <summary>書き出す: 出力先が決まっていればそこへ、無ければ毎回ファイルダイアログでたずねる（F-109）。</summary>
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var export = ViewModel.Export;
        var path = export.TargetPathInFolder();
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Markdown で書き出す",
                FileName = export.SuggestFileName(),
                DefaultExt = ".md",
                AddExtension = true,
                Filter = "Markdown (*.md)|*.md",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OverwritePrompt = true,
            };
            if (!ShowDialog(dialog))
            {
                return;
            }
            path = dialog.FileName;
        }
        await export.ExportAsync(path);
    }

    /// <summary>出力先のフォルダを決める（次からダイアログを出さずにそこへ書く）。</summary>
    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var export = ViewModel.Export;
        var dialog = new OpenFolderDialog
        {
            Title = "Markdown の出力先のフォルダ",
            InitialDirectory = export.Folder is { } folder && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (ShowDialog(dialog))
        {
            export.SetFolder(dialog.FolderName);
        }
    }

    private bool ShowDialog(CommonDialog dialog) =>
        (Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
}
