using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Residency;

/// <summary>
/// 開発用フォルダ（AppPaths.IsDevelopment）だけで働く確認の入口。本番では何もしない。
/// 環境変数 TASKDECK_DEV_RESIDENCY（カンマ区切り）:
///   quick      … 起動が落ち着いたらクイック入力窓を開く
///   notify[:N] … 通知日時を過ぎたタスクを N 件（既定 1）足す。起動直後の確認で出る（2件以上はまとめて1件。アプリ内の知らせ）
///   trayicons  … トレイのアイコンの絵（ライト・ダークのタスクバー）とメニューの絵を &lt;データフォルダ&gt;\dev-tray\ に書き出す
/// 合図のファイル（データフォルダにできたら動く。ホットキーやトレイを押さずに確かめるため）:
///   dev-exit  … 終了する（トレイがあると × では終わらないため。Stop-Process で止めるとトレイに消えないアイコンが残る）
///   dev-quick … クイック入力窓を開く（窓の使い回し・2回目の表示を確かめる。消してから作り直すと、もう一度開く）
/// </summary>
internal sealed class ResidencyDevTools(
    AppPaths paths,
    ShellService shell,
    ITaskRepository tasks,
    IClock clock,
    TrayViewModel trayViewModel,
    ILogger<ResidencyDevTools> logger) : IHostedService, IDisposable
{
    public const string Variable = "TASKDECK_DEV_RESIDENCY";
    public const string ExitSignal = "dev-exit";
    public const string QuickSignal = "dev-quick";

    private FileSystemWatcher? _watcher;
    private bool _exiting;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!paths.IsDevelopment)
        {
            return;
        }
        WatchSignals();
        foreach (var mode in (Environment.GetEnvironmentVariable(Variable) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = mode.Split(':', 2);
            switch (parts[0].ToLowerInvariant())
            {
                case "quick":
                    _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, shell.OpenQuickInput);
                    break;
                case "notify":
                    await AddDueRemindersAsync(parts.Length > 1 && int.TryParse(parts[1], out var count) ? count : 1, cancellationToken);
                    break;
                case "trayicons":
                    ExportTrayIcons();
                    ExportTrayMenu();
                    break;
                default:
                    logger.LogWarning("{Variable} の {Mode} は知らない値です", Variable, mode);
                    break;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _watcher?.Dispose();

    private void WatchSignals()
    {
        foreach (var name in new[] { ExitSignal, QuickSignal })
        {
            var stale = Path.Combine(paths.DataDirectory, name);
            if (File.Exists(stale))
            {
                File.Delete(stale); // 前回の合図の残り（合図を受けたときは、書いている側がまだ開いていることがあるので消さない）
            }
        }
        // ファイルの出来た・消えたで十分。書き込み（DB の -wal など）まで受けると、書き込みが多いときに通知の溜め場所があふれて合図を取りこぼす
        _watcher = new FileSystemWatcher(paths.DataDirectory, "dev-*") { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Created += (_, e) => Application.Current?.Dispatcher.BeginInvoke(() => OnSignal(e.Name));
    }

    private void OnSignal(string? name)
    {
        if (_exiting)
        {
            return;
        }
        switch (name)
        {
            case ExitSignal:
                _exiting = true;
                logger.LogInformation("開発用の終了の合図（{Signal}）を受けたので終了します", ExitSignal);
                shell.ExitApplication();
                break;
            case QuickSignal:
                logger.LogInformation("開発用の合図（{Signal}）でクイック入力を開きます", QuickSignal);
                shell.OpenQuickInput();
                break;
        }
    }

    private async Task AddDueRemindersAsync(int count, CancellationToken ct)
    {
        var now = clock.UtcNow;
        for (var i = 1; i <= count; i++)
        {
            await tasks.AddAsync(new NewTaskRequest
            {
                Title = $"通知の見本 {i}",
                DueAt = now.AddMinutes(30),
                DueHasTime = true,
                RemindAt = now.AddMinutes(-1),
            }, ct);
        }
        logger.LogInformation("通知の見本を {Count} 件足しました", count);
    }

    /// <summary>ライト・ダークのタスクバーの地に、状態ごとのアイコン（16px を4倍・32px を2倍）を並べた1枚。</summary>
    private void ExportTrayIcons()
    {
        TrayState[] states =
        [
            new(null, false, ""),
            new(new TrayBadge(7, false), false, ""),
            new(new TrayBadge(3, true), false, ""),
            new(new TrayBadge(12, false), false, ""),
            new(new TrayBadge(120, true), false, ""),
            new(new TrayBadge(5, false), true, ""),
            new(null, true, ""),
        ];
        const int cell = 150;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            foreach (var (dark, row) in new[] { (false, 0), (true, 1) })
            {
                var background = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
                var accent = dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, row * 90, cell * states.Length, 90));
                for (var i = 0; i < states.Length; i++)
                {
                    dc.DrawImage(TrayIconRenderer.Render(states[i], dark, accent, 16), new Rect((i * cell) + 8, (row * 90) + 13, 64, 64));
                    dc.DrawImage(TrayIconRenderer.Render(states[i], dark, accent, 32), new Rect((i * cell) + 80, (row * 90) + 13, 64, 64));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(cell * states.Length, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        SavePng(bitmap, "tray-icons.png");
    }

    /// <summary>
    /// トレイのメニューを、開かずに（窓の外で並べて）絵にする。確認用の起動ではメニューを開けない
    /// （開くとマウスを捕まえ、依頼者のクリックを1回横取りしうる）ため。
    /// </summary>
    private void ExportTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        TrayIconService.Fill(menu, trayViewModel.BuildMenu());
        // 影の余白ぶん中身が右下へずれて描かれるので、広めの枠で並べて全体を入れる
        var area = new Size(360, 320);
        menu.Measure(area);
        menu.Arrange(new Rect(new Point(0, 0), new Size(Math.Min(area.Width, menu.DesiredSize.Width + 40), Math.Min(area.Height, menu.DesiredSize.Height + 40))));
        menu.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        SavePng(bitmap, "tray-menu.png");
    }

    private void SavePng(BitmapSource bitmap, string name)
    {
        var folder = Path.Combine(paths.DataDirectory, "dev-tray");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file))
        {
            encoder.Save(stream);
        }
        logger.LogInformation("確認用の絵を書き出しました: {File}", file);
    }
}
