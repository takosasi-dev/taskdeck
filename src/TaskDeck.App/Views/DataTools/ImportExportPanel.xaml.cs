using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TaskDeck.Data.ImportExport;
using UiControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace TaskDeck.App.Views.DataTools;

/// <summary>
/// 設定画面「データ」のインポート／エクスポートのカード（部品）。XAML から new してそのまま置ける（ViewModel は DI から取る）。
/// 保存先・読み込み元はファイルダイアログで選び、置換だけは確認ダイアログを出す（F-144）。処理そのものは ViewModel。
/// </summary>
public partial class ImportExportPanel : UserControl
{
    private const string JsonFilter = "JSON ファイル (*.json)|*.json";
    private const string CsvFilter = "CSV ファイル (*.csv)|*.csv";

    public ImportExportPanel()
    {
        InitializeComponent();
        if (AppServices.IsReady)
        {
            // UserControl 自身の DataContext はホストのもの。中身だけ ViewModel につなぐ
            Root.DataContext = AppServices.Get<ImportExportViewModel>();
        }
    }

    internal ImportExportViewModel ViewModel => (ImportExportViewModel)Root.DataContext;

    /// <summary>JSON に書き出す（ダイアログで選んだ後の処理。確認用の窓は既定のパスで直接呼ぶ）。</summary>
    internal Task ExportJsonToAsync(string path) => ViewModel.ExportJsonAsync(path);

    /// <summary>JSON を読んで検査し、置換なら確認してから書き込む。</summary>
    internal async Task ImportFromAsync(string path)
    {
        var package = await ViewModel.ReadAsync(path);
        if (package is null)
        {
            return;   // 読めない・検査で止まった（理由はカードに出ている）
        }
        var mode = ViewModel.SelectedMode;
        if (mode == ImportMode.Replace && !await ConfirmReplaceAsync(package, path))
        {
            ViewModel.ReportReplaceDeclined();
            return;
        }
        await ViewModel.ImportAsync(package, mode);
    }

    internal Task ExportCsvToAsync(string path) => ViewModel.ExportCsvAsync(path);

    private async void OnExportJson(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "JSON で書き出す",
            FileName = ViewModel.SuggestJsonFileName(),
            DefaultExt = ".json",
            AddExtension = true,
            Filter = JsonFilter,
        };
        if (ShowDialog(dialog))
        {
            await ExportJsonToAsync(dialog.FileName);
        }
    }

    private async void OnImportJson(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "JSON を読み込む",
            Filter = JsonFilter + "|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };
        if (ShowDialog(dialog))
        {
            await ImportFromAsync(dialog.FileName);
        }
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "表示中の一覧を CSV で書き出す",
            FileName = ViewModel.SuggestCsvFileName(),
            DefaultExt = ".csv",
            AddExtension = true,
            Filter = CsvFilter,
        };
        if (ShowDialog(dialog))
        {
            await ExportCsvToAsync(dialog.FileName);
        }
    }

    private bool ShowDialog(FileDialog dialog) =>
        (Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    /// <summary>置換の確認（F-144: 取り消せない操作だけ確かめる）。ファイルの件数を見せて、置き換えるかを聞く。</summary>
    private async Task<bool> ConfirmReplaceAsync(ImportPackage package, string path)
    {
        var message = string.Format(
            CultureInfo.CurrentCulture,
            "「{0}」の内容（タスク {1:N0} 件・プロジェクト {2:N0} 件・タグ {3:N0} 件・テンプレート {4:N0} 件）で置き換えます。\n\n"
                + "現在のデータはすべて置き換わります。直前の状態はバックアップに残します。",
            Path.GetFileName(path),
            package.TaskCount,
            package.ProjectCount,
            package.TagCount,
            package.TemplateCount);
        var box = new UiMessageBox
        {
            Title = "データを置き換えますか",
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Style = (Style)FindResource("TextBodySmallStyle"),
            },
            PrimaryButtonText = "置き換える",
            PrimaryButtonAppearance = UiControlAppearance.Danger,
            CloseButtonText = "やめる",
            Owner = Window.GetWindow(this),
            // 既定は主モニタの中央。設定画面の上に出す
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        return await box.ShowDialogAsync() == UiMessageBoxResult.Primary;
    }
}
