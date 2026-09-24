using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Services;

/// <summary>
/// settings.json の読み書き（ISettingsStore）。変更は即保存（一時ファイルに書いてから置き換える）。
/// 壊れていたら settings.json.bak にリネームして既定値で起動する（ファイルは消さない）。
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // 保存済みフィルタの名前などの日本語を \uXXXX にせず、そのまま読める形で書く
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        // 型の情報はソース生成のものを使う（起動のたびにリフレクションで組み立てない。NFR 3.2）。ほかの型に使われたときはリフレクションで補う
        TypeInfoResolver = JsonTypeInfoResolver.Combine(SettingsJsonContext.Default, new DefaultJsonTypeInfoResolver()),
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load(out var problem);
        LoadProblem = problem;
    }

    public AppSettings Current { get; }

    /// <summary>読み込み時に起きた問題（壊れていたので退避した等）。ロガーができてから記録するために持っておく。</summary>
    public string? LoadProblem { get; }

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            problem = $"settings.json が壊れていたため settings.json.bak に退避し、既定値で起動しました: {ex.Message}";
            File.Move(_path, _path + ".bak", overwrite: true);
            return new AppSettings();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>settings.json の型の情報（ソース生成）。</summary>
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
