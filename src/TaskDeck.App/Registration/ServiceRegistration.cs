using Microsoft.Extensions.DependencyInjection;
using TaskDeck.App.Services;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;
using TaskDeck.Data;

namespace TaskDeck.App.Registration;

/// <summary>
/// DI の登録。共通部分はこのファイル（全員触らない）、画面ごとの登録は担当ごとの
/// ServiceRegistration.*.cs の partial メソッドに書く（担当どうしで同じファイルを触らないため）。
/// </summary>
internal static partial class ServiceRegistration
{
    public static void AddAll(IServiceCollection services, AppPaths paths, SettingsStore settings)
    {
        services.AddSingleton(paths);
        services.AddSingleton(settings);
        services.AddSingleton<ISettingsStore>(settings);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<DataChangeHub>();
        services.AddSingleton<UndoStack>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ShellService>();

        services.AddSingleton<QuickInputParser>();
        services.AddSingleton<IRecurrenceEngine, RecurrenceEngine>();
        services.AddSingleton<TemplateExpander>();
        services.AddSingleton<StatsCalculator>();

        services.AddTaskDeckData(new TaskDeckDataOptions(paths.DatabasePath, paths.BackupDirectory));

        AddShell(services);
        AddAppearance(services);
        AddTemplates(services);
        AddDataTools(services);
        AddResidency(services);
        AddCalendar(services);
        AddPalette(services);
        AddInsights(services);
        AddScratch(services);
        AddStartup(services);
    }

    /// <summary>使い捨てリスト（波3-N）。</summary>
    static partial void AddScratch(IServiceCollection services);

    /// <summary>初回起動（波4-L。起動画面は DI の前に出すので登録しない）。</summary>
    static partial void AddStartup(IServiceCollection services);

    /// <summary>メイン画面（波1-C）。</summary>
    static partial void AddShell(IServiceCollection services);

    /// <summary>テーマ・ピッカー・設定画面（波1-D）。</summary>
    static partial void AddAppearance(IServiceCollection services);

    /// <summary>繰り返し設定・テンプレート画面（波2-F）。</summary>
    static partial void AddTemplates(IServiceCollection services);

    /// <summary>入出力・複合絞り込み（波2-G）。</summary>
    static partial void AddDataTools(IServiceCollection services);

    /// <summary>トレイ・ホットキー・クイック入力・通知・自動起動（波3-H）。</summary>
    static partial void AddResidency(IServiceCollection services);

    /// <summary>カレンダー（波3-I）。</summary>
    static partial void AddCalendar(IServiceCollection services);

    /// <summary>コマンドパレット・ショートカット一覧・フォーカスモード（波3-J）。</summary>
    static partial void AddPalette(IServiceCollection services);

    /// <summary>振り返り・Markdown・外部API（波3-K）。</summary>
    static partial void AddInsights(IServiceCollection services);
}
