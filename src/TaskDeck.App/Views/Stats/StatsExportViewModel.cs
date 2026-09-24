using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data.Export;

namespace TaskDeck.App.Views.Stats;

/// <summary>Markdown で書き出す期間。</summary>
public enum ExportRange
{
    Today,
    ThisWeek,
    ThisMonth,
    ThisYear,
}

/// <summary>
/// 振り返りの画面の「Markdown で書き出す」（F-108・F-109）。担当: 波3-K。
/// 期間（今日・今週・今月・今年）に完了したタスクを、完了した日ごとの「- [x] タイトル」で書く（中身は MarkdownExporter）。
/// 出力先は settings.Data.MarkdownExportFolder。null なら毎回ファイルダイアログでたずねる（ダイアログは画面が出す）。
/// 期間は、パネルを開くまでは振り返りで選んでいる期間に合わせる。
/// </summary>
public sealed partial class StatsExportViewModel(
    MarkdownExporter exporter,
    ISettingsStore settings,
    IClock clock,
    ILogger<StatsExportViewModel> logger) : ObservableObject
{
    /// <summary>書き出しのパネルを開いているか。</summary>
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToday), nameof(IsWeek), nameof(IsMonth), nameof(IsYear), nameof(RangeText))]
    private ExportRange _range = ExportRange.ThisWeek;

    /// <summary>結果の一言（書き出した・0件・書けなかった）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _isError;

    /// <summary>最後に書き出したファイル（「フォルダを開く」「次からもこのフォルダに」に使う）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenLastFolder), nameof(CanRememberLastFolder))]
    [NotifyCanExecuteChangedFor(nameof(OpenLastFolderCommand), nameof(RememberLastFolderCommand))]
    private string? _lastWrittenPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportToFolderCommand))]
    private bool _isBusy;

    public bool IsToday
    {
        get => Range == ExportRange.Today;
        set => SelectIf(value, ExportRange.Today);
    }

    public bool IsWeek
    {
        get => Range == ExportRange.ThisWeek;
        set => SelectIf(value, ExportRange.ThisWeek);
    }

    public bool IsMonth
    {
        get => Range == ExportRange.ThisMonth;
        set => SelectIf(value, ExportRange.ThisMonth);
    }

    public bool IsYear
    {
        get => Range == ExportRange.ThisYear;
        set => SelectIf(value, ExportRange.ThisYear);
    }

    public bool HasMessage => Message is not null;

    /// <summary>決めてある出力先（null なら毎回たずねる）。</summary>
    public string? Folder => settings.Current.Data.MarkdownExportFolder;

    public bool HasFolder => Folder is not null;

    public string FolderText => Folder ?? "書き出すときに毎回たずねます";

    /// <summary>期間の日付（「9月20日（日）〜 9月26日（土）」）。</summary>
    public string RangeText
    {
        get
        {
            var (from, to) = Dates();
            return from == to ? Day(from) : $"{Day(from)} 〜 {Day(to)}";
        }
    }

    public bool CanOpenLastFolder => LastWrittenPath is not null;

    /// <summary>ダイアログで選んだ場所に書いた後だけ「次からもこのフォルダに書き出す」を出す。</summary>
    public bool CanRememberLastFolder =>
        LastWrittenPath is { } path && !string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>振り返りで期間を選んだ（パネルを開いていなければ、書き出す期間もそれに合わせる）。</summary>
    public void OnPeriodShown(StatsPeriod period)
    {
        if (!IsOpen)
        {
            Range = period switch
            {
                StatsPeriod.ThisMonth => ExportRange.ThisMonth,
                StatsPeriod.ThisYear => ExportRange.ThisYear,
                _ => ExportRange.ThisWeek,
            };
        }
    }

    /// <summary>期間の最初と最後の日（ローカル日付。週の始まりは設定に従う）。</summary>
    public (DateOnly From, DateOnly To) Dates()
    {
        var today = clock.LocalToday();
        switch (Range)
        {
            case ExportRange.Today:
                return (today, today);
            case ExportRange.ThisMonth:
                var first = new DateOnly(today.Year, today.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            case ExportRange.ThisYear:
                return (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31));
            default:
                var monday = settings.Current.Calendar.WeekStartsOnMonday;
                var start = today.AddDays(-(monday ? ((int)today.DayOfWeek + 6) % 7 : (int)today.DayOfWeek));
                return (start, start.AddDays(6));
        }
    }

    public string SuggestFileName()
    {
        var (from, to) = Dates();
        return MarkdownExporter.SuggestFileName(from, to);
    }

    /// <summary>決めてある出力先のファイル（毎回たずねるなら null）。</summary>
    public string? TargetPathInFolder() => Folder is { } folder ? Path.Combine(folder, SuggestFileName()) : null;

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen)
        {
            Message = null;
        }
    }

    private bool CanExportToFolder() => !IsBusy;

    /// <summary>決めてある出力先に書く（画面は Folder が null ならダイアログで選んでから <see cref="ExportAsync"/> を呼ぶ）。</summary>
    [RelayCommand(CanExecute = nameof(CanExportToFolder))]
    private Task ExportToFolderAsync() => TargetPathInFolder() is { } path ? ExportAsync(path) : Task.CompletedTask;

    /// <summary>期間に完了したタスクを path に書く。0件ならファイルを作らない。</summary>
    public async Task ExportAsync(string path)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var (from, to) = Dates();
            var document = await exporter.BuildAsync(from, to);
            if (document.TaskCount == 0)
            {
                Show("この期間に完了したタスクはありません（ファイルは作っていません）", isError: false);
                return;
            }
            await MarkdownExporter.WriteAsync(path, document);
            LastWrittenPath = path;
            Show($"書き出しました（{document.TaskCount:N0} 件）: {Path.GetFileName(path)}", isError: false);
        }
        catch (DirectoryNotFoundException ex)
        {
            logger.LogWarning(ex, "Markdown の出力先のフォルダが見つかりませんでした");
            Show("出力先のフォルダが見つかりません。フォルダを選び直してください", isError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Markdown を書き出せませんでした");
            Show("書き出せませんでした。書き込めるフォルダか、ほかのアプリで開いていないか確かめてください", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>出力先を決める（null で「毎回たずねる」に戻す。F-109）。</summary>
    public void SetFolder(string? folder)
    {
        settings.Update(s => s.Data.MarkdownExportFolder = string.IsNullOrWhiteSpace(folder) ? null : folder);
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(CanRememberLastFolder));
    }

    [RelayCommand]
    private void AskEveryTime() => SetFolder(null);

    [RelayCommand(CanExecute = nameof(CanRememberLastFolder))]
    private void RememberLastFolder()
    {
        if (LastWrittenPath is { } path)
        {
            SetFolder(Path.GetDirectoryName(path));
            RememberLastFolderCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>書き出したファイルをエクスプローラで選んだ状態で開く。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLastFolder))]
    private void OpenLastFolder()
    {
        if (LastWrittenPath is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
    }

    private void SelectIf(bool selected, ExportRange range)
    {
        if (selected)
        {
            Range = range;
        }
    }

    private void Show(string message, bool isError)
    {
        IsError = isError;
        Message = message;
    }

    private static string Day(DateOnly date) =>
        $"{date.Month}月{date.Day}日（{"日月火水木金土"[(int)date.DayOfWeek]}）";
}
