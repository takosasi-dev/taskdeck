using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TaskDeck.App.Controls;
using TaskDeck.App.Services;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Settings;
using Wpf.Ui.Controls;

namespace TaskDeck.App.Views.DataTools;

/// <summary>
/// 開発用の確認窓（DataToolsDevStartup が開く。本番では開かない）。
/// 「既定のパス」は開発用データフォルダの隣の transfer フォルダ（.devdata\transfer）で、別の開発用データフォルダからも同じファイルを読める。
/// 絞り込みを適用すると ShellService.CurrentListQuery にも入れて、メイン画面の一覧の代わりにする（CSV の「表示中の一覧」の確認用）。
/// </summary>
public partial class DataToolsDevWindow : FluentWindow
{
    private readonly ShellService _shell;
    private readonly ISettingsStore _settings;
    private readonly string _transferFolder;
    private TaskQuery _query = BuiltInViews.QueryFor(ViewKey.All) with { SortKey = TaskSortKey.Due };

    public DataToolsDevWindow(AppPaths paths, ShellService shell, ISettingsStore settings)
    {
        _shell = shell;
        _settings = settings;
        InitializeComponent();
        _transferFolder = Path.Combine(Path.GetDirectoryName(paths.DataDirectory) ?? paths.DataDirectory, "transfer");
        TransferFolderText.Text = $"既定のパス: {JsonPath} ／ {CsvPath}";
        settings.Changed += OnSettingsChanged;
        Closed += (_, _) => settings.Changed -= OnSettingsChanged;
        ShowQuery();
        ShowSavedFilters();
    }

    private string JsonPath => Path.Combine(_transferFolder, "export.json");

    private string CsvPath => Path.Combine(_transferFolder, "list.csv");

    private async void OnDevExportJson(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_transferFolder);
        await Transfer.ExportJsonToAsync(JsonPath);
    }

    private async void OnDevAppend(object sender, RoutedEventArgs e)
    {
        Transfer.ViewModel.IsReplaceMode = false;
        await Transfer.ImportFromAsync(JsonPath);
    }

    private async void OnDevReplace(object sender, RoutedEventArgs e)
    {
        Transfer.ViewModel.IsReplaceMode = true;
        await Transfer.ImportFromAsync(JsonPath);
    }

    private async void OnDevExportCsv(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_transferFolder);
        await Transfer.ExportCsvToAsync(CsvPath);
    }

    private void OnOpenFilter(object sender, RoutedEventArgs e)
    {
        Filter.Query = _query;
        FilterPopup.IsOpen = true;
    }

    private void OnFilterApplied(object? sender, FilterAppliedEventArgs e)
    {
        _query = e.Query;
        _shell.CurrentListQuery = _query;
        FilterPopup.IsOpen = false;
        ShowQuery();
    }

    private void OnFilterCancelled(object? sender, EventArgs e) => FilterPopup.IsOpen = false;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _query = _query with { SearchText = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text };
        ShowQuery();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowSavedFilters);

    private void ShowQuery() => QueryText.Text = JsonSerializer.Serialize(_query, SettingsStore.JsonOptions);

    private void ShowSavedFilters() =>
        SavedFiltersText.Text = "保存済みフィルタ（settings.json）: "
            + (_settings.Current.SavedFilters.Count == 0 ? "なし" : string.Join("、", _settings.Current.SavedFilters.Select(f => f.Name)));
}
