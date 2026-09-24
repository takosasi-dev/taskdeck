using TaskDeck.Core.Abstractions;

namespace TaskDeck.Tests.Scratch;

/// <summary>メモリ上の AppState（書いた回数も数える）。</summary>
internal sealed class MemoryAppState : IAppStateRepository
{
    public Dictionary<string, string> Values { get; } = [];

    public int Writes { get; private set; }

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        Writes++;
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
