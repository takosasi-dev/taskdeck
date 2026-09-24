using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Scratch;

/// <summary>
/// 使い捨てリストの保存役。全リストを JSON にして AppState の <see cref="AppStateKeys.ScratchLists"/> に1件で持つ。
/// 中身はメモリ上のリスト（<see cref="Lists"/>）が正で、画面はそれを直接書き換えて <see cref="MarkDirty"/> し、
/// 保存（<see cref="SaveAsync"/>）は画面の側が少し待ってから・窓とアプリを閉じるときに呼ぶ。書き換えは UI スレッドから。
/// リストが増えた・名前が変わった・捨てられたときは <see cref="Changed"/> で知らせる（DataChangeHub の種類は増やさない）。
/// </summary>
public sealed class ScratchStore(IAppStateRepository state, IClock clock)
{
    public const string DefaultName = "新しいリスト";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 日本語を \uXXXX にせず、そのまま読める形で書く
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        AllowTrailingCommas = true,
    };

    private readonly List<ScratchList> _lists = [];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Task? _loading;
    private long _snapshot;
    private long _written;

    /// <summary>作った順。</summary>
    public IReadOnlyList<ScratchList> Lists => _lists;

    /// <summary>まだ保存していない変更がある。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>読み込みで起きた問題（保存内容が読めなかった等）。ロガーを持つ側が記録する。</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>読めなかった保存内容そのもの（空の状態で始める前に、呼び出し側が退避する）。</summary>
    public string? BrokenJson { get; private set; }

    public event EventHandler<ScratchChangedEventArgs>? Changed;

    /// <summary>保存内容を読む（2回目からは何もしない）。</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    public ScratchList? Find(Guid id) => _lists.Find(l => l.Id == id);

    /// <summary>リストを作る。名前が空なら「新しいリスト」、同じ名前があれば「 2」「 3」…を付ける。</summary>
    public ScratchList Create(string? name, IEnumerable<ScratchItem>? items = null)
    {
        var clean = CleanName(name);
        var list = new ScratchList
        {
            Name = UniqueName(clean.Length > 0 ? clean : DefaultName),
            CreatedAt = clock.UtcNow,
            Items = [.. items ?? []],
        };
        ScratchOutline.Normalize(list.Items);
        _lists.Add(list);
        IsDirty = true;
        Changed?.Invoke(this, new ScratchChangedEventArgs(ScratchChange.Created, list.Id));
        return list;
    }

    /// <summary>名前を変える。空（空白だけ）や同じ名前なら何もしないで false（空にしたら元の名前のまま）。</summary>
    public bool Rename(Guid id, string? name)
    {
        var clean = CleanName(name);
        if (Find(id) is not { } list || clean.Length == 0 || clean == list.Name)
        {
            return false;
        }
        list.Name = clean;
        IsDirty = true;
        Changed?.Invoke(this, new ScratchChangedEventArgs(ScratchChange.Renamed, id));
        return true;
    }

    /// <summary>リストを捨てる（取り消しは無い。完了済みにも振り返りにも残らない）。</summary>
    public bool Discard(Guid id)
    {
        if (Find(id) is not { } list)
        {
            return false;
        }
        _lists.Remove(list);
        IsDirty = true;
        Changed?.Invoke(this, new ScratchChangedEventArgs(ScratchChange.Discarded, id));
        return true;
    }

    /// <summary>項目を書き換えた（文字・チェック・字下げ・行の増減）。</summary>
    public void MarkDirty() => IsDirty = true;

    /// <summary>
    /// 変更があれば保存する。写しは呼び出したスレッド（UI）で取り、書き込みは1つずつ（古い写しで新しい写しを上書きしない）。
    /// 書き込みに失敗したら変更ありに戻して例外をそのまま上げる（次の保存でもう一度書く）。
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (!IsDirty)
        {
            return;
        }
        var json = JsonSerializer.Serialize(_lists, JsonOptions);
        var number = ++_snapshot;
        IsDirty = false;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (number > _written)
            {
                await state.SetAsync(AppStateKeys.ScratchLists, json, ct).ConfigureAwait(false);
                _written = number;
            }
        }
        catch
        {
            IsDirty = true;
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>書きかけの保存を含めて保存し切る（窓・アプリを閉じるとき）。</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await SaveAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        _writeGate.Release();
    }

    private async Task LoadCoreAsync()
    {
        var json = await state.GetAsync(AppStateKeys.ScratchLists);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }
        List<ScratchList?>? lists;
        try
        {
            lists = JsonSerializer.Deserialize<List<ScratchList?>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            LoadProblem = $"使い捨てリストの保存内容が読めなかったため、空の状態で始めます（{ex.Message}）";
            BrokenJson = json;
            return;
        }
        foreach (var list in lists ?? [])
        {
            if (list is null || Find(list.Id) is not null)
            {
                continue;
            }
            var name = CleanName(list.Name);
            list.Name = name.Length > 0 ? name : DefaultName;
            // 手で直された JSON でも落ちないように、欠けた値を埋めて深さを整える
            list.Items = [.. (list.Items ?? []).OfType<ScratchItem>()];
            foreach (var item in list.Items)
            {
                item.Text ??= "";
            }
            ScratchOutline.Normalize(list.Items);
            _lists.Add(list);
        }
    }

    /// <summary>名前はテンプレート名にもなるので、テンプレート名と同じ規則（NFKC・前後の空白・100文字）にそろえる。</summary>
    private static string CleanName(string? name) =>
        TextNormalizer.TruncateGraphemes(TextNormalizer.ForName(name), TaskTemplate.NameMaxLength);

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; _lists.Exists(l => l.Name == candidate); n++)
        {
            candidate = $"{name} {n}";
        }
        return candidate;
    }
}
