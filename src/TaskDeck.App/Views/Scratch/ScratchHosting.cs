using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Scratch;

namespace TaskDeck.App.Views.Scratch;

/// <summary>
/// 起動時に使い捨てリストと小窓の設定を読み、アプリを閉じるときに保存し切る（打った直後に閉じても残るように）。
/// 保存内容が読めなかったときは、空で始める前にデータフォルダへ元の文字を退避する（消さない）。
/// </summary>
internal sealed class ScratchLifetime(ScratchStore store, ScratchPresenter presenter, AppPaths paths, ILogger<ScratchLifetime> logger) : IHostedService
{
    public const string BrokenFileName = "scratch-lists.broken.json";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.LoadAsync();
        if (store.LoadProblem is { } problem)
        {
            var path = Path.Combine(paths.DataDirectory, BrokenFileName);
            try
            {
                File.WriteAllText(path, store.BrokenJson ?? "", new UTF8Encoding(false));
                logger.LogWarning("{Problem}。元の内容は {Path} に退避しました", problem, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 退避できなくても起動は止めない（元の内容は DB のバックアップにも残っている）
                logger.LogError(ex, "{Problem}。元の内容を {Path} に退避できませんでした", problem, path);
            }
        }
        await presenter.LoadAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 終了処理は UI スレッドを止めたまま（App.OnExit の GetResult）待つので、UI スレッドに戻らないスレッドで保存する。
            // UI スレッドは止まっているので、この間にリストが書き換わることはない
            await Task.Run(() => presenter.FlushAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "終了までに使い捨てリストを保存し切れませんでした");
        }
    }
}

/// <summary>
/// 開発時だけの確認用の入口。開発用データフォルダ（TASKDECK_DATA_DIR）かつ TASKDECK_DEV_SCRATCH があるときだけ働く。本番では登録もしない。
/// window: 見本のリスト（リストがあれば最初のもの）を小窓で開く ／ main: メイン画面の中央に出す ／ templates: テンプレート画面を開く。
/// </summary>
internal sealed class ScratchDevLauncher(AppPaths paths, ScratchPresenter presenter, ShellService shell, ILogger<ScratchDevLauncher> logger) : IHostedService
{
    public const string Variable = "TASKDECK_DEV_SCRATCH";

    public static bool IsRequested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (paths.IsDevelopment)
        {
            var mode = Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant();
            // StartAsync は画面を出す前に呼ばれるので、起動処理が終わってから動かす（失敗は画面の未処理例外としてログに残る）
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Run(mode)));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run(string? mode)
    {
        logger.LogInformation("確認用: 使い捨てリストを {Mode} で開きます", mode);
        switch (mode)
        {
            case "window":
                presenter.ShowInWindow(SampleList().Id);
                break;
            case "main":
                // メイン画面の読み込みの途中でも、読み込みが済んでから開く（MainViewModel.NavigateAsync）
                presenter.ShowInMain(SampleList().Id);
                break;
            case "templates":
                shell.OpenTemplates();
                break;
            default:
                logger.LogWarning("確認用: {Variable} は window / main / templates のどれかにしてください", Variable);
                break;
        }
    }

    /// <summary>リストがあれば最初のもの（起動し直しても残っているかを見るため）、無ければ見本を作る。</summary>
    private ScratchList SampleList()
    {
        if (presenter.Store.Lists.FirstOrDefault() is { } existing)
        {
            return existing;
        }
        var list = presenter.Store.Create("買い物",
        [
            new ScratchItem { Text = "牛乳", IsChecked = true },
            new ScratchItem { Text = "卵" },
            new ScratchItem { Text = "パン" },
            new ScratchItem { Text = "食パン 6枚切り", Depth = 1 },
        ]);
        presenter.ScheduleSave();
        return list;
    }
}
