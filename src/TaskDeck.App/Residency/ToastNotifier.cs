using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.Core.Reminders;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Residency;

/// <summary>通知を出す口（本物は <see cref="ToastNotifier"/>。ReminderWorker のテストでは記録するだけの偽物）。どのスレッドから呼んでもよい。</summary>
public interface IReminderNotifier
{
    /// <summary>見回り1回ぶんの通知をまとめて渡す（アプリ内の知らせは1件ずつ差し替わるので、同時に出すものは1行にまとめるため）。</summary>
    void Show(IReadOnlyList<ReminderNotice> notices);
}

/// <summary>
/// 通知を出す（F-081〜083、UI 設計書 14.1・14.2）。Windows のトースト（CommunityToolkit の ToastNotificationManagerCompat）か、
/// メイン画面が前面にあるときはアプリ内の知らせ（ShellService.NotifyUser。メイン画面の下に出る）にする。
/// クイック入力や設定だけが前面のとき（メイン画面は隠れている）は、アプリ内の知らせが見えないのでトーストにする。
/// 判断と表示は UI スレッドで行う（呼び出し側の見回りは待たない）。
/// 個別の通知のボタンは「完了にする」を左、「N分後に再通知」を右（ボタンは2つまで）。本文のクリックでそのタスクを見せる。
/// 押された中身は OnActivated で受けて <see cref="ReminderActions"/> に渡す（止まっている間に押されて -ToastActivated で起動したときも、
/// 起動時に購読しているのでここに来る）。
/// 開発用フォルダでは TASKDECK_DEV_TOASTS=1 のときだけトーストを使う。ToastNotificationManagerCompat はレジストリ（AUMID・COM）に
/// 書くので、それ以外では型に触れもしない（同じ中身をアプリ内の知らせとログに出す）。
/// 開発用フォルダで使ったときは、終了時に登録と出した通知を消し（Uninstall）、出す通知も10分で消えるようにする。
/// 残った通知を後から押すと、環境変数の無い -ToastActivated の起動になり、本番のデータフォルダ・自動起動・ホットキーで動いてしまうため
/// （強制終了で Uninstall が走らなかったときの逃げ道。確実に防ぐには App.OnStartup 側の判定が要る。統合担当への依頼に書いた）。
/// </summary>
internal sealed class ToastNotifier(
    ShellService shell,
    ISettingsStore settings,
    ResidencySwitches switches,
    ReminderActions actions,
    IClock clock,
    ILogger<ToastNotifier> logger) : IReminderNotifier, IDisposable
{
    /// <summary>開発用フォルダで出した通知が通知センターに残る時間。</summary>
    private static readonly TimeSpan DevExpiration = TimeSpan.FromMinutes(10);

    private Dispatcher? _dispatcher;
    private bool _subscribed;

    /// <summary>起動時に UI スレッドで1回。トーストを使う構成なら押されたときの受け口をつなぐ。</summary>
    public void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (!switches.ShowToasts)
        {
            logger.LogInformation("開発用フォルダのため、Windows の通知は使いません（TASKDECK_DEV_TOASTS=1 で使う）。通知はアプリ内の知らせに出します");
            return;
        }
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            _subscribed = true;
        }
        catch (Exception ex) when (IsToastFailure(ex))
        {
            // 通知は任意の機能。登録できなくても起動は続け、通知はアプリ内の知らせに出す
            logger.LogWarning(ex, "Windows の通知の受け口を登録できませんでした");
        }
    }

    /// <summary>Windows の通知まわり（COM・レジストリ）で起こりうる失敗。</summary>
    private static bool IsToastFailure(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or IOException or SecurityException
            or System.ComponentModel.Win32Exception or InvalidOperationException;

    public void Dispose()
    {
        if (!_subscribed)
        {
            return;
        }
        ToastNotificationManagerCompat.OnActivated -= OnActivated;
        _subscribed = false;
        if (!switches.IsDevelopment)
        {
            return;
        }
        try
        {
            ToastNotificationManagerCompat.Uninstall();
            logger.LogInformation("開発用フォルダで使った Windows の通知の登録と、出した通知を消しました");
        }
        catch (Exception ex) when (IsToastFailure(ex))
        {
            logger.LogWarning(ex, "開発用フォルダで使った Windows の通知の登録を消せませんでした");
        }
    }

    public void Show(IReadOnlyList<ReminderNotice> notices)
    {
        if (notices.Count == 0)
        {
            return;
        }
        if ((_dispatcher ?? Application.Current?.Dispatcher) is not { } dispatcher)
        {
            logger.LogWarning("画面の準備前のため通知を出せませんでした（{Count} 件）", notices.Count);
            return;
        }
        // 終了の途中（UI スレッドが止まっている）でも見回りを待たせないよう、投げたら戻る
        dispatcher.BeginInvoke(() => ShowOnUiThread(notices));
    }

    private void ShowOnUiThread(IReadOnlyList<ReminderNotice> notices)
    {
        var inApp = !_subscribed || shell.MainWindow is { IsActive: true };
        foreach (var notice in notices)
        {
            logger.LogInformation("通知: {Kind}（{Count} 件 {Ids}）→ {Where}",
                notice.Kind, notice.TaskIds.Count, string.Join(",", notice.TaskIds.Take(10)), inApp ? "アプリ内の知らせ" : "Windows の通知");
        }
        var rest = inApp ? notices : ShowToasts(notices);
        if (rest.Count > 0)
        {
            // アプリ内の知らせは同時に1件（UI 設計書 13.1）なので、同じ回に出すものは1行につなぐ
            shell.NotifyUser(string.Join("　", rest.Select(n => n.InAppText)));
        }
    }

    /// <summary>トーストで出す。出せなかった分（OS で通知が切られている・失敗した）を返す（アプリ内の知らせに回す）。</summary>
    private IReadOnlyList<ReminderNotice> ShowToasts(IReadOnlyList<ReminderNotice> notices)
    {
        var shown = 0;
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != Windows.UI.Notifications.NotificationSetting.Enabled)
            {
                // OS の設定で切られていると Show は黙って捨てるので、先に確かめる
                logger.LogInformation("Windows の通知が OS の設定で止められているため（{Setting}）、アプリ内の知らせに出します", notifier.Setting);
                return notices;
            }
            foreach (var notice in notices)
            {
                var toast = new Windows.UI.Notifications.ToastNotification(Build(notice).GetXml());
                if (switches.IsDevelopment)
                {
                    toast.ExpirationTime = new DateTimeOffset(clock.UtcNow + DevExpiration);
                }
                notifier.Show(toast);
                shown++;
            }
            return [];
        }
        catch (Exception ex) when (IsToastFailure(ex))
        {
            logger.LogWarning(ex, "Windows の通知を出せなかったので、残りをアプリ内の知らせに出します");
            return [.. notices.Skip(shown)];
        }
    }

    private ToastContentBuilder Build(ReminderNotice notice)
    {
        var builder = new ToastContentBuilder()
            .AddText(notice.Title, hintMaxLines: 1)
            .AddText(notice.Body);
        if (notice.Kind == ReminderNoticeKind.Single)
        {
            var id = notice.TaskIds[0].ToString();
            var snooze = settings.Current.Notifications.SnoozeMinutes;
            builder
                .AddArgument("action", ReminderActions.Open)
                .AddArgument("id", id)
                .AddButton(new ToastButton()
                    .SetContent("完了にする")
                    .AddArgument("action", ReminderActions.Complete)
                    .AddArgument("id", id)
                    .SetBackgroundActivation())
                .AddButton(new ToastButton()
                    .SetContent(SnoozeLabel(snooze))
                    .AddArgument("action", ReminderActions.Snooze)
                    .AddArgument("id", id)
                    .AddArgument("minutes", snooze)
                    .SetBackgroundActivation());
        }
        else
        {
            builder.AddArgument("action", "today");
        }
        return builder;
    }

    /// <summary>「30分後に再通知」「1時間後に再通知」。</summary>
    public static string SnoozeLabel(int minutes) => minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60}時間後に再通知" : $"{minutes}分後に再通知";

    /// <summary>トーストが押された（別スレッドで来る）。UI スレッドへ移して処理する。</summary>
    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        arguments.TryGetValue("action", out string? action);
        Guid? taskId = arguments.TryGetValue("id", out string? idText) && Guid.TryParse(idText, out var parsed) ? parsed : null;
        int? minutes = arguments.TryGetValue("minutes", out string? minutesText) && int.TryParse(minutesText, out var m) ? m : null;
        logger.LogInformation("通知が押されました: {Action} {TaskId}", action, taskId);
        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => HandleOnUiThread(action, taskId, minutes));
    }

    // 例外は Dispatcher の未処理例外ハンドラ（App.xaml.cs）でログに残る
    private async void HandleOnUiThread(string? action, Guid? taskId, int? minutes) => await actions.HandleAsync(action, taskId, minutes);
}
