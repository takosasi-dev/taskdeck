using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Settings;
using TaskDeck.Data.External;

namespace TaskDeck.App.Controls;

/// <summary>リンク1件（タイトルが無ければ URL をそのまま出す）。</summary>
public sealed record LinkRow(string Url, string? Title)
{
    public string DisplayTitle => Title ?? Url;

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "";

    public string AutomationName => $"リンクを開く: {DisplayTitle}";

    public override string ToString() => DisplayTitle;
}

/// <summary>
/// メモ内の URL のタイトル一覧（F-196、既定 OFF）。詳細ペインのメモの下に置く。担当: 波3-K。
/// 公開 API（変えない）: Text（メモの本文）。
/// 設定の「メモの URL のタイトルを取得する」が ON で、オフラインでないときだけ出す（OFF・オフラインなら何も出さない）。
/// まず URL を並べ、Microlink でタイトルが取れたら差し替える（キャッシュは30日）。押すと既定のブラウザで開く。
/// </summary>
public partial class LinkPreviewList : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LinkPreviewList), new PropertyMetadata(null, (d, _) => ((LinkPreviewList)d).Refresh()));

    private CancellationTokenSource? _pending;
    private ISettingsStore? _settings;

    public LinkPreviewList()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (AppServices.IsReady && _settings is null)
            {
                _settings = AppServices.Get<ISettingsStore>();
                _settings.Changed += OnSettingsChanged;
            }
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (_settings is not null)
            {
                _settings.Changed -= OnSettingsChanged;
                _settings = null;
            }
        };
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Refresh);

    // 例外は Dispatcher の未処理例外ハンドラ（App.xaml.cs）まで上げてログに残す（DB が読めないときだけここで受ける）
    private async void Refresh()
    {
        _pending?.Cancel();
        var service = AppServices.IsReady ? AppServices.Get<LinkPreviewService>() : null;
        var urls = service is { IsActive: true } ? LinkPreviewService.ExtractUrls(Text) : [];
        if (service is null || urls.Count == 0)
        {
            LinkItems.ItemsSource = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        LinkItems.ItemsSource = urls.Select(url => new LinkRow(url, null)).ToList();
        Visibility = Visibility.Visible;
        using var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        try
        {
            var items = await Task.Run(() => service.ResolveAsync(urls, cancellation.Token));
            if (!cancellation.IsCancellationRequested)
            {
                LinkItems.ItemsSource = items.Select(item => new LinkRow(item.Url, item.Title)).ToList();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 別のタスクを開いた（新しい Refresh が引き継いだ）
        }
        catch (SqliteException ex)
        {
            // タイトルのキャッシュが読めないだけ。URL はそのまま出ている
            AppServices.Get<ILogger<LinkPreviewList>>().LogWarning(ex, "URL のタイトルのキャッシュを読めませんでした");
        }
        finally
        {
            if (ReferenceEquals(_pending, cancellation))
            {
                _pending = null;
            }
        }
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LinkRow row
            || !Uri.TryCreate(row.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;   // http/https 以外は開かない（NFR 6.5）
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            // 既定のブラウザが無いなど。URL はログに出さない（メモの中身のため）
            AppServices.Get<ILogger<LinkPreviewList>>().LogWarning(ex, "リンクを開けませんでした");
        }
    }
}
