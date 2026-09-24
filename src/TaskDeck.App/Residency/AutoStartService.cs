using System.IO;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Residency;

/// <summary>HKCU の Run の読み書き。本物は <see cref="RegistryRunKey"/>、テストでは偽物を渡す。</summary>
public interface IRunKey
{
    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run（本番だけで使う）。</summary>
internal sealed class RegistryRunKey : IRunKey
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Windows ログオン時の自動起動（F-094）。HKCU の Run に <c>"&lt;exe のパス&gt;" --tray</c> を書く／消す。
/// settings.General.LaunchAtLogin（既定 ON）に合わせて、起動時と設定を変えたときに当てる。ON の間は起動のたびに
/// 今の exe のパスへ書き直す（置き場所を移しても追いつく）。exe のパスは Environment.ProcessPath（単一 exe の配布でもその exe）。
/// 開発用フォルダでは絶対に書かない・消さない（書くはずだった値をログに出すだけ。INTERFACES 5.9）。
/// </summary>
public sealed class AutoStartService(
    ISettingsStore settings,
    ResidencySwitches switches,
    IRunKey runKey,
    ILogger<AutoStartService> logger,
    string? exePath = null) : IDisposable
{
    public const string ValueName = "TaskDeck";

    private readonly string? _exePath = exePath ?? Environment.ProcessPath;
    private readonly Lock _gate = new();
    private string? _applied;
    private bool _started;

    public static string CommandFor(string exePath) => $"\"{exePath}\" --tray";

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        settings.Changed += OnSettingsChanged;
        Apply(force: true);
    }

    public void Dispose() => settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => Apply(force: false);

    private void Apply(bool force)
    {
        lock (_gate)
        {
            ApplyLocked(force);
        }
    }

    private void ApplyLocked(bool force)
    {
        var wanted = settings.Current.General.LaunchAtLogin && _exePath is { } exe ? CommandFor(exe) : null;
        if (!force && wanted == _applied)
        {
            return; // 自動起動に関係ない設定の変更
        }
        _applied = wanted;
        if (!switches.WriteRunKey)
        {
            logger.LogInformation("開発用フォルダのため、自動起動を Windows に登録しません（書くはずだった値: {Value}）", wanted ?? "（Run から消す）");
            return;
        }
        try
        {
            var current = runKey.Get(ValueName);
            if (wanted is null)
            {
                if (current is not null)
                {
                    runKey.Delete(ValueName);
                    logger.LogInformation("自動起動をやめました");
                }
            }
            else if (current != wanted)
            {
                runKey.Set(ValueName, wanted);
                logger.LogInformation("自動起動を登録しました: {Value}", wanted);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // レジストリに書けないだけならアプリは続ける（次の起動でまた試す）
            logger.LogWarning(ex, "自動起動の登録を変えられませんでした");
        }
    }
}
