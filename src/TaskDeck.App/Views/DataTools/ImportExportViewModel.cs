using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;
using TaskDeck.Data.ImportExport;

namespace TaskDeck.App.Views.DataTools;

/// <summary>
/// 設定画面「データ」のインポート／エクスポートのカード（F-104〜F-106）の状態と処理。
/// ファイルダイアログと置換の確認は画面（ImportExportPanel）が出し、ここはパスを受け取って裏で処理する。
/// 処理中は IsBusy（ボタンを止める）と進み具合、終わったら結果か失敗の理由を StatusMessage に出す。
/// </summary>
public sealed partial class ImportExportViewModel(
    JsonExporter exporter,
    JsonImporter importer,
    CsvExporter csv,
    ShellService shell,
    UndoStack undo,
    IClock clock,
    ILogger<ImportExportViewModel> logger) : ObservableObject
{
    private const string CannotWrite = "ファイルに書き込めませんでした。ほかのアプリ（Excel など）で開いていれば閉じてから、もう一度試してください。書き込めない場所なら、別の場所を選んでください。";
    private const string CannotRead = "ファイルを読めませんでした。ファイルがあるか、ほかのアプリで開いていないか確かめてください。";

    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowStatus))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus), nameof(HasStatusMessage))]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>JSON の読み込み方。false なら「追加」、true なら「置換」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppendMode))]
    private bool _isReplaceMode;

    public bool IsIdle => !IsBusy;

    public bool ShowStatus => IsBusy || StatusMessage is not null;

    public bool HasStatusMessage => StatusMessage is not null;

    /// <summary>RadioButton の「追加」側（IsReplaceMode の裏返し）。</summary>
    public bool IsAppendMode
    {
        get => !IsReplaceMode;
        set => IsReplaceMode = !value;
    }

    public ImportMode SelectedMode => IsReplaceMode ? ImportMode.Replace : ImportMode.Append;

    public string SuggestJsonFileName() => $"TaskDeck_{Stamp()}.json";

    public string SuggestCsvFileName() => $"TaskDeck_一覧_{Stamp()}.csv";

    /// <summary>JSON で書き出す（F-104）。途中で失敗・取り消しても、半端なファイルは残さない。</summary>
    public Task ExportJsonAsync(string path) =>
        RunAsync("JSON を書き出しています", "書き出しを取り消しました。", CannotWrite, async (progress, ct) =>
        {
            var s = await WriteFileAsync(path, stream => exporter.ExportAsync(stream, progress, ct));
            return Done(Text(
                $"書き出しました（タスク {s.TaskCount:N0} 件・プロジェクト {s.ProjectCount:N0} 件・タグ {s.TagCount:N0} 件・テンプレート {s.TemplateCount:N0} 件）: {Path.GetFileName(path)}"));
        });

    /// <summary>JSON を読んで検査する（まだ書き込まない）。問題があれば理由を出して null。</summary>
    public async Task<ImportPackage?> ReadAsync(string path)
    {
        ImportPackage? package = null;
        await RunAsync("ファイルを読んでいます", "読み込みを取り消しました。", CannotRead, async (_, ct) =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var result = await importer.ReadAsync(stream, ct);
            package = result.Value;
            return result.Succeeded ? (null, false) : (result.Error, true);
        });
        return package;
    }

    /// <summary>検査を通したファイルを書き込む（F-105）。置換の確認は呼ぶ前に済ませておく。</summary>
    public Task ImportAsync(ImportPackage package, ImportMode mode) =>
        RunAsync(
            mode == ImportMode.Replace ? "置き換えています" : "追加しています",
            "読み込みを取り消しました。データは変わっていません。",
            CannotRead,
            async (progress, ct) =>
            {
                var result = await importer.ImportAsync(package, mode, progress, ct);
                if (!result.Succeeded)
                {
                    return (result.Error, true);
                }
                if (mode == ImportMode.Replace)
                {
                    // 置き換える前のデータに対する取り消しは、もう当てられない（消えたタスクを作り直してしまう）
                    undo.Clear();
                }
                return Done(Describe(result.Value!));
            });

    /// <summary>メイン画面の一覧を、見えている条件と並びのまま CSV に書く（F-106）。一覧を出していなければ「すべて」。</summary>
    public Task ExportCsvAsync(string path) =>
        RunAsync("CSV を書き出しています", "書き出しを取り消しました。", CannotWrite, async (_, ct) =>
        {
            var count = await WriteFileAsync(path, stream => csv.ExportAsync(shell.CurrentListQuery, stream, ct));
            return Done(Text($"CSV に書き出しました（{count:N0} 行）: {Path.GetFileName(path)}"));
        });

    /// <summary>置換の確認で「やめる」を選んだとき。</summary>
    public void ReportReplaceDeclined() => Show("置き換えをやめました。データは変わっていません。", isError: false);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// 処理の共通の枠: ボタンを止め、進み具合を出し、終わったら結果（null なら何も出さない）か失敗の理由を出す。
    /// ファイルが開けない・書けないは fileErrorMessage にする（DB の失敗は各サービスが結果として返す）。
    /// Excel で開いているファイルへの上書きは「権限なし」で失敗するので、権限の失敗も同じ文にまとめる。
    /// </summary>
    private async Task RunAsync(
        string stage,
        string cancelledMessage,
        string fileErrorMessage,
        Func<IProgress<DataTransferProgress>, CancellationToken, Task<(string? Message, bool IsError)>> work)
    {
        if (IsBusy)
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        StatusMessage = null;
        ProgressText = stage;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        IsBusy = true;
        try
        {
            // Progress は UI スレッドで作るので、報告は UI スレッドに戻ってくる
            var (message, isError) = await work(new Progress<DataTransferProgress>(OnProgress), cancellation.Token);
            if (message is not null)
            {
                Show(message, isError);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("データの入出力を取り消しました: {Stage}", stage);
            Show(cancelledMessage, isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "ファイルを読み書きできませんでした: {Stage}", stage);
            Show(fileErrorMessage, isError: true);
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }
    }

    private void OnProgress(DataTransferProgress progress)
    {
        if (!IsBusy)
        {
            return;   // 終わった後に届いた報告
        }
        ProgressText = progress.Total > 0 ? Text($"{progress.Stage}（{progress.Done:N0} / {progress.Total:N0}）") : progress.Stage;
        IsProgressIndeterminate = progress.Total <= 0;
        ProgressValue = progress.Total > 0 ? 100d * progress.Done / progress.Total : 0;
    }

    private void Show(string message, bool isError)
    {
        IsStatusError = isError;
        StatusMessage = message;
    }

    private static (string? Message, bool IsError) Done(string message) => (message, false);

    /// <summary>結果の文（件数・既存に寄せた数・飛ばしたもの・バックアップの場所）。</summary>
    private static string Describe(ImportSummary s)
    {
        var lines = new List<string>
        {
            Text($"{(s.Mode == ImportMode.Replace ? "置き換えました" : "追加しました")}（タスク {s.TaskCount:N0} 件・プロジェクト {s.ProjectCount:N0} 件・タグ {s.TagCount:N0} 件・テンプレート {s.TemplateCount:N0} 件）。"),
        };
        if (s.MergedProjectCount + s.MergedTagCount > 0)
        {
            lines.Add(Text($"同じ名前のプロジェクト {s.MergedProjectCount:N0} 件・タグ {s.MergedTagCount:N0} 件は、今あるものにまとめました。"));
        }
        if (s.Skipped.Count > 0)
        {
            lines.Add("入れなかったもの: " + string.Join("、", s.Skipped) + "。");
        }
        if (s.BackupPath is { } backup)
        {
            lines.Add($"置き換える前の状態はバックアップ（{Path.GetFileName(backup)}）に残しています。");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 一時ファイルに書いてから置き換える（途中で失敗・取り消しても、元のファイルを壊さず半端なファイルも残さない）。
    /// </summary>
    private static async Task<T> WriteFileAsync<T>(string path, Func<Stream, Task<T>> write)
    {
        var temp = path + ".tmp";
        try
        {
            T result;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                result = await write(stream);
            }
            File.Move(temp, path, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string Stamp() => clock.LocalNow().ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);
}
