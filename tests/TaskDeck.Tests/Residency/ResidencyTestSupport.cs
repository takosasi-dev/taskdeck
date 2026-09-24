using TaskDeck.App.Residency;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Reminders;

namespace TaskDeck.Tests.Residency;

/// <summary>RegisterHotKey の偽物。TakenKeys の仮想キーは「他のアプリが使用中」として失敗させる。</summary>
internal sealed class FakeHotkeyApi : IHotkeyApi
{
    public Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = [];

    public HashSet<uint> TakenKeys { get; } = [];

    public int RegisterCalls { get; private set; }

    public bool IsDisposed { get; private set; }

    public event EventHandler<int>? Pressed;

    public int? Register(int id, uint modifiers, uint virtualKey)
    {
        RegisterCalls++;
        if (TakenKeys.Contains(virtualKey))
        {
            return 1409; // ERROR_HOTKEY_ALREADY_REGISTERED
        }
        Registered[id] = (modifiers, virtualKey);
        return null;
    }

    public bool Unregister(int id) => Registered.Remove(id);

    public void Press(int id) => Pressed?.Invoke(this, id);

    public void Dispose() => IsDisposed = true;
}

/// <summary>UI スレッドへ戻す代わりに、その場で実行する。</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>HKCU の Run の偽物（本物のレジストリに触らない）。</summary>
internal sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = [];

    public int Writes { get; private set; }

    public string? Get(string name) => Values.GetValueOrDefault(name);

    public void Set(string name, string value)
    {
        Writes++;
        Values[name] = value;
    }

    public void Delete(string name)
    {
        Writes++;
        Values.Remove(name);
    }
}

/// <summary>メモリ上の AppState（キーと値。クイック入力の履歴の確認用）。</summary>
internal sealed class ResidencyAppState : IAppStateRepository
{
    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        if (value is null)
        {
            Values.Remove(key);
        }
        else
        {
            Values[key] = value;
        }
        return Task.CompletedTask;
    }
}

/// <summary>出した通知を覚えておくだけの通知先。</summary>
internal sealed class RecordingNotifier : IReminderNotifier
{
    public List<ReminderNotice> Shown { get; } = [];

    public void Show(IReadOnlyList<ReminderNotice> notices) => Shown.AddRange(notices);
}

internal static class Switches
{
    /// <summary>本番の構成（ホットキー・トースト・Run をすべて本物に使う）。</summary>
    public static ResidencySwitches Production { get; } = new(IsDevelopment: false, RegisterHotkeys: true, ShowToasts: true, NoActivate: false);

    /// <summary>開発用フォルダで何も付けずに起動したとき。</summary>
    public static ResidencySwitches Development { get; } = new(IsDevelopment: true, RegisterHotkeys: false, ShowToasts: false, NoActivate: true);
}
