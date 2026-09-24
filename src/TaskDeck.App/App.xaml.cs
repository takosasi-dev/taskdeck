using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using TaskDeck.App.Interop;
using TaskDeck.App.Registration;
using TaskDeck.App.Services;
using TaskDeck.App.Views.Startup;
using TaskDeck.Core.Abstractions;
using TaskDeck.Data;
using TaskDeck.Data.Backup;
using TaskDeck.Data.External;
using TaskDeck.Data.Infrastructure;
using TaskDeck.Data.Maintenance;
using TaskDeck.Data.Seeding;

namespace TaskDeck.App;

/// <summary>
/// 起動の順序（全員触らない）: 多重起動の確認 → 設定 → 起動画面（専用のスレッド）→ ログ → DI
/// → DB（バックアップ→更新。スレッドプール）と並べて、テーマとメイン画面の組み立て（UI スレッド）→ 常駐処理の開始
/// → 画面（初回なら初回起動の3画面）→ その窓が描けたら起動画面を消す。
/// 未処理例外は3か所で受けてログに残し、アプリは落とさない（設計書 6.1）。
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Resolve();

        // 開発ビルド（exe の隣に TaskDeck.dll がある＝単一 exe の配布ではない）の残った通知が後から押されると、
        // 環境変数なしで起動されて本番のデータ・自動起動・ホットキーに触れてしまうので、何もせずに終わる
        if (e.Args.Contains("-ToastActivated") && !paths.IsDevelopment
            && File.Exists(Path.Combine(AppContext.BaseDirectory, "TaskDeck.dll")))
        {
            Shutdown();
            return;
        }

        _instance = SingleInstance.TryAcquire(paths.InstanceKey);
        if (_instance is null)
        {
            SingleInstance.SignalExisting(paths.InstanceKey, e.Args);
            Shutdown();
            return;
        }

        paths.EnsureDirectories();
        var settings = new SettingsStore(paths.SettingsPath);
        // 起動画面（S-16）。専用のスレッドで描くので、ここで出せば後の UI スレッドの仕事と並んで動く。窓を出さない起動（自動起動・通知から）では出さない
        var splash = ShellService.IsBackgroundStart(e.Args)
            ? null
            : StartupSplash.Show(settings.Current.Appearance.ReduceMotion ?? !SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);
        ConfigureLogging(paths, settings.Current.General.VerboseLogging);
        Log.Information("TaskDeck {Version} を起動します（データ: {DataDir}, 開発モード: {Dev}）",
            typeof(App).Assembly.GetName().Version, paths.DataDirectory, paths.IsDevelopment);
        if (settings.LoadProblem is not null)
        {
            Log.Warning("{Problem}", settings.LoadProblem);
        }
        RegisterGlobalExceptionHandlers();

        var elapsed = Stopwatch.StartNew();

#pragma warning disable CA1031 // 最上位のハンドラ: 起動に失敗したらログを残し、ログの場所を示して終了する
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Services.AddSerilog(dispose: false);
            ServiceRegistration.AddAll(builder.Services, paths, settings);
            _host = builder.Build();
            AppServices.Provider = _host.Services;
            var services = _host.Services;
            var hostReady = elapsed.ElapsedMilliseconds;

            // DB の準備と読み込みはスレッドプールで（どれも画面に触らない）。その間に UI スレッドでテーマを当て、メイン画面を組み立てておく（NFR 3.2）。
            // DB の更新が無い起動では、起動時バックアップは画面を出してから裏で取る（RunMaintenanceAsync）
            var dataReady = 0L;
            var data = Task.Run(async () =>
            {
                var warmUp = Task.Run(() => WarmUpQueries(services));
                var result = await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(deferBackupWhenCurrent: true);
                // 初回のデータ・祝日・天気は互いに関係しないので並べる（SQLite の非同期は中身が同期なので、それぞれスレッドプールへ）
                await Task.WhenAll(
                    warmUp,
                    Task.Run(() => SeedFirstRunAsync(services, paths)),
                    Task.Run(() => services.GetRequiredService<HolidayCache>().ReloadAsync()),
                    Task.Run(() => services.GetRequiredService<WeatherCache>().ReloadAsync()));
                dataReady = elapsed.ElapsedMilliseconds;
                return result;
            });
            var shell = services.GetRequiredService<ShellService>();
            var prepared = 0L;
            try
            {
                services.GetRequiredService<ThemeService>().ApplyFromSettings();
                if (!ShellService.IsBackgroundStart(e.Args))
                {
                    shell.PrepareMainWindow();
                }
                prepared = elapsed.ElapsedMilliseconds;
            }
            finally
            {
                // 画面の用意に失敗しても、DB の準備（更新に失敗したときのバックアップからの戻しを含む）は終わるまで待つ（途中で終了させない）
                await Task.WhenAny(data);
            }
            var db = await data;
            await _host.StartAsync();
            var firstRun = await services.GetRequiredService<FirstRunService>().ShouldShowAsync();

            _instance.StartListening(
                args => Dispatcher.BeginInvoke(() => shell.HandleActivation(args)),
                services.GetRequiredService<ILogger<App>>());
            var started = elapsed.ElapsedMilliseconds;
            var shown = shell.StartUp(e.Args, firstRun);
            Log.Information("起動の内訳（ms、ログの開始から）: DI {Host}・メイン画面の用意 {Prepared}・DB と読み込み {Data}・常駐の開始 {Started}・画面を出すまで {Shown}",
                hostReady, prepared, dataReady, started, elapsed.ElapsedMilliseconds);
            if (splash is not null)
            {
                CloseSplashWhenRendered(splash, shown);
            }
            _ = Task.Run(() => RunMaintenanceAsync(services, db.BackupDeferred));
        }
        catch (DatabaseMigrationException ex)
        {
            Log.Fatal(ex, "DB の更新に失敗しました");
            splash?.Close();
            ShowFatal($"{ex.Message}\n\nデータは起動前の状態のままです。ログを確認してください。", paths);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "起動に失敗しました");
            splash?.Close();
            ShowFatal("TaskDeck を起動できませんでした。\n\nログを確認してください。", paths);
            Shutdown(1);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// 起動画面は、次に出した窓が最初の1枚を描いてから消す（間に何も無い時間を作らない）。
    /// 描かれないまま（出してすぐ最小化されたなど）でも、5秒で消す。
    /// </summary>
    private static void CloseSplashWhenRendered(StartupSplash splash, Window? shown)
    {
        if (shown is null)
        {
            splash.Close();
            return;
        }
        shown.ContentRendered += (_, _) => splash.Close();
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => splash.Close(), TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            Log.Warning(ex, "終了処理が時間内に終わりませんでした");
        }
        finally
        {
            // 時間切れでも破棄は必ず行う（ホットキーの解除・トレイのアイコン・通知の登録の後片付けはここで走る）
            _host?.Dispose();
        }
        Log.Information("TaskDeck を終了します（終了コード {Code}）", e.ApplicationExitCode);
        Log.CloseAndFlush();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 初回だけ入れるもの: 初期テンプレート3件と、サンプル3件（F-184。既にタスクがあれば入れない）。
    /// 開発用データフォルダでは環境変数 TASKDECK_DEV_SEED=件数 で、サンプルの代わりに性能確認用の大量データを入れる。
    /// </summary>
    private static async Task SeedFirstRunAsync(IServiceProvider services, AppPaths paths)
    {
        await services.GetRequiredService<ITemplateRepository>().SeedDefaultsAsync();
        if (paths.IsDevelopment
            && int.TryParse(Environment.GetEnvironmentVariable("TASKDECK_DEV_SEED"), out var count)
            && await services.GetRequiredService<IAppStateRepository>().GetAsync(AppStateKeys.SampleDataSeeded) is null)
        {
            await services.GetRequiredService<DevDataSeeder>().SeedAsync(count);
        }
        await services.GetRequiredService<SampleDataSeeder>().SeedIfNeededAsync();
    }

    /// <summary>
    /// EF のクエリの組み立ては、アプリで最初の1回だけ重い（約0.2秒）。DB の更新の確認と並べて、先に温めておく。
    /// 本物の DB には触らない（メモリ上の空の DB に投げ、表が無いという失敗は捨てる。組み立ては投げる前に済んでいる）。
    /// 設定は本物と同じ形（インターセプタ付き）にして、EF の内部の部品を使い回させる。
    /// </summary>
    private static void WarmUpQueries(IServiceProvider services)
    {
#pragma warning disable CA1031 // 温めは失敗しても、本物の最初のクエリが同じ組み立てをするだけ。起動を止めないよう何でも捨てる
        try
        {
            var options = new DbContextOptionsBuilder<TaskDeckDbContext>()
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(services.GetRequiredService<AuditInterceptor>())
                .Options;
            using var db = new TaskDeckDbContext(options);
            _ = db.SyncMeta.Any();
        }
        catch (SqliteException)
        {
            // 表が無い（空の DB なので当然）
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "EF のクエリの温めに失敗しました（起動は続ける）");
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// 起動時の保守。起動を遅らせないよう、画面が出てから裏で回す: 後回しにした起動時バックアップ（2秒後。画面の最初の読み込みとぶつけない）
    /// → F-107（30日超のゴミ箱の物理削除・月1回の VACUUM。10秒後）。
    /// </summary>
    private static async Task RunMaintenanceAsync(IServiceProvider services, bool backupDeferred)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
#pragma warning disable CA1031 // 裏で回す保守の最上位: 失敗はログに残してアプリは続ける（次の起動でまた試す）
        try
        {
            if (backupDeferred)
            {
                services.GetRequiredService<BackupService>().CreateStartupBackup();
            }
            await Task.Delay(TimeSpan.FromSeconds(8));
            await services.GetRequiredService<DatabaseMaintenance>().RunStartupAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "起動時の保守に失敗しました");
        }
#pragma warning restore CA1031
    }

    private static void ConfigureLogging(AppPaths paths, bool verbose)
    {
        // タスクのタイトル・メモはログに出さない（ID のみ）。EF のSQLパラメータも出さない設定のまま使う
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "taskdeck-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "監視されていないタスクの例外");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "未処理の例外で終了します");
            Log.CloseAndFlush();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // アプリを落とさずに続ける。書き込みはトランザクション単位なので、失敗した操作は反映されていない
        Log.Error(e.Exception, "画面の処理で未処理の例外");
        e.Handled = true;
        if (AppServices.IsReady)
        {
            AppServices.Get<ShellService>().NotifyUser("問題が発生したため、直前の操作は反映されていません（ログに記録しました）");
        }
    }

    private static void ShowFatal(string message, AppPaths paths)
    {
        var result = MessageBox.Show(
            message + "\n\n[OK] でログのフォルダを開きます。",
            "TaskDeck",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Error);
        if (result == MessageBoxResult.OK && Directory.Exists(paths.LogDirectory))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{paths.LogDirectory}\"") { UseShellExecute = true });
        }
    }
}
