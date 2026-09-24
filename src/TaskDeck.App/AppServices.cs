using Microsoft.Extensions.DependencyInjection;

namespace TaskDeck.App;

/// <summary>
/// XAML から new される UserControl（ピッカー・カレンダーなど）がサービスを受け取るための入口。
/// ViewModel や通常のクラスではコンストラクタで受け取ること（ここを使わない）。
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Provider
    {
        get => _provider ?? throw new InvalidOperationException("サービスの準備前に AppServices が使われました。");
        internal set => _provider = value;
    }

    /// <summary>デザイナ上など、まだ準備されていないときは false。</summary>
    public static bool IsReady => _provider is not null;

    public static T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
}
