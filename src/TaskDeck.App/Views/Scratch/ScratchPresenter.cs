using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Templates;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Scratch;

namespace TaskDeck.App.Views.Scratch;

/// <summary>使い捨てリストを出す場所。</summary>
public enum ScratchPlace
{
    /// <summary>常駐の小窓（最初はこちら）。</summary>
    Window,
    /// <summary>メイン画面の中央。</summary>
    Main,
}

/// <summary>AppStateKeys.ScratchWindow に JSON で持つもの（最後に使った場所と、小窓の位置・大きさ・ピン）。</summary>
public sealed class ScratchWindowState
{
    public ScratchPlace LastPlace { get; set; } = ScratchPlace.Window;

    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = 340;

    public double Height { get; set; } = 460;

    /// <summary>常に手前に固定する。</summary>
    public bool Pinned { get; set; }
}

/// <summary>
/// 使い捨てリストをどこに出すか（メイン画面の中央か小窓か）を決めて出す。「新しいリスト」「使い捨てで開く」は
/// 最後に使った方で開く（最初は小窓）。小窓は1つだけで、いま小窓に出しているリストは <see cref="WindowListId"/>。
/// 項目の保存もここでまとめる（打つたびに少し待ってから保存し、窓とアプリを閉じるときは保存し切る）。
/// </summary>
public sealed class ScratchPresenter
{
    /// <summary>打つのが止まってから保存するまでの時間。</summary>
    public const int SaveDelayMs = 400;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppStateRepository _appState;
    private readonly ITemplateRepository _templates;
    private readonly ShellService _shell;
    private readonly IServiceProvider _services;
    private readonly ILogger<ScratchPresenter> _logger;
    private readonly Debouncer _saveDebouncer;
    private ScratchWindow? _window;
    private Task _stateSave = Task.CompletedTask;
    private bool _saveFailureShown;

    public ScratchPresenter(
        ScratchStore store,
        IAppStateRepository appState,
        ITemplateRepository templates,
        ShellService shell,
        IServiceProvider services,
        ILogger<ScratchPresenter> logger)
    {
        Store = store;
        _appState = appState;
        _templates = templates;
        _shell = shell;
        _services = services;
        _logger = logger;
        _saveDebouncer = new Debouncer(() => SaveListsAsync(flush: false));
    }

    public ScratchStore Store { get; }

    public ScratchWindowState WindowState { get; private set; } = new();

    /// <summary>いま小窓に出しているリスト（小窓が無ければ null）。</summary>
    public Guid? WindowListId { get; private set; }

    /// <summary>小窓に出しているリストが変わった（開いた・切り替えた・閉じた）。</summary>
    public event EventHandler? WindowListChanged;

    /// <summary>最後に使った場所と小窓の位置を読む（起動時）。</summary>
    public async Task LoadAsync()
    {
        var json = await _appState.GetAsync(AppStateKeys.ScratchWindow);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }
        try
        {
            WindowState = JsonSerializer.Deserialize<ScratchWindowState>(json, JsonOptions) ?? new ScratchWindowState();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "使い捨てリストの小窓の設定が読めなかったため、既定の値で始めます");
        }
    }

    public ScratchListViewModel CreateListViewModel(ScratchList list) => new(list, Store, _templates, ScheduleSave, _logger);

    /// <summary>項目を書き換えた。打つのが止まってから保存する。</summary>
    public void ScheduleSave()
    {
        Store.MarkDirty();
        _saveDebouncer.Schedule(SaveDelayMs);
    }

    /// <summary>保存し切る（窓・アプリを閉じるとき）。</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await SaveListsAsync(flush: true, ct);
        await _stateSave;
    }

    /// <summary>空のリストを作る（まだどこにも出さない）。</summary>
    public ScratchList CreateNew()
    {
        var list = Store.Create(null);
        ScheduleSave();
        _logger.LogInformation("使い捨てリスト {ListId} を作りました", list.Id);
        return list;
    }

    /// <summary>テンプレートの項目（題名と階層だけ）からリストを作る。タスクは作らない。テンプレートが無ければ null。</summary>
    public async Task<ScratchList?> CreateFromTemplateAsync(Guid templateId)
    {
        if (await _templates.GetWithItemsAsync(templateId) is not { } template)
        {
            return null;
        }
        var list = Store.Create(template.Template.Name, ScratchTemplates.FromTemplate(template.Items));
        ScheduleSave();
        _logger.LogInformation("テンプレート {TemplateId} から使い捨てリスト {ListId} を作りました（項目 {Count} 件）", templateId, list.Id, list.Items.Count);
        return list;
    }

    /// <summary>最後に使った方（メイン画面か小窓か）で出す。</summary>
    public void Open(Guid listId)
    {
        if (WindowState.LastPlace == ScratchPlace.Main)
        {
            ShowInMain(listId);
        }
        else
        {
            ShowInWindow(listId);
        }
    }

    /// <summary>メイン画面の中央に出す（小窓に出していたら小窓を閉じる）。</summary>
    public void ShowInMain(Guid listId)
    {
        if (WindowListId == listId)
        {
            _window?.Close();
        }
        NoteShownIn(ScratchPlace.Main);
        _shell.NavigateTo(ViewKey.ForScratch(listId));
    }

    /// <summary>小窓に出す（小窓は1つ。開いていればそのリストに切り替える）。</summary>
    public void ShowInWindow(Guid listId)
    {
        NoteShownIn(ScratchPlace.Window);
        if (_window is null)
        {
            _window = _services.GetRequiredService<ScratchWindow>();
            _window.ApplyPlacement(WindowState);
            _window.Closed += OnWindowClosed;
        }
        _window.ViewModel.Show(listId);
        if (_window.WindowState == System.Windows.WindowState.Minimized)
        {
            _window.WindowState = System.Windows.WindowState.Normal;
        }
        _shell.ShowToolWindow(_window);
    }

    /// <summary>リストをこの場所に出した（メイン画面でサイドバーから選んだときも）。次の「新しいリスト」はここで開く。</summary>
    public void NoteShownIn(ScratchPlace place)
    {
        if (WindowState.LastPlace != place)
        {
            WindowState.LastPlace = place;
            SaveWindowState();
        }
    }

    /// <summary>小窓の位置・大きさ・ピンを覚える（小窓が閉じるとき・ピンを変えたとき）。</summary>
    public void SaveWindowState()
    {
        var json = JsonSerializer.Serialize(WindowState, JsonOptions);
        _stateSave = SaveStateAsync(_stateSave, json);
    }

    /// <summary>小窓の中身が変わった（小窓の ViewModel が呼ぶ）。</summary>
    internal void SetWindowList(Guid? listId)
    {
        if (WindowListId == listId)
        {
            return;
        }
        WindowListId = listId;
        WindowListChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        SetWindowList(null);
        await SaveListsAsync(flush: true);
    }

    private async Task SaveStateAsync(Task previous, string json)
    {
        await previous;
        try
        {
            await _appState.SetAsync(AppStateKeys.ScratchWindow, json);
        }
        catch (Exception ex) when (TemplatesViewModel.IsDataError(ex))
        {
            _logger.LogError(ex, "使い捨てリストの小窓の設定を保存できませんでした");
        }
    }

    /// <summary>
    /// 保存する。失敗したらログに残して1回だけ知らせる（変更ありのまま残るので、次の変更・閉じるときにもう一度書く）。
    /// </summary>
    private async Task SaveListsAsync(bool flush, CancellationToken ct = default)
    {
        try
        {
            if (flush)
            {
                await Store.FlushAsync(ct);
            }
            else
            {
                await Store.SaveAsync(ct);
            }
            _saveFailureShown = false;
        }
        catch (Exception ex) when (TemplatesViewModel.IsDataError(ex))
        {
            _logger.LogError(ex, "使い捨てリストを保存できませんでした");
            if (!_saveFailureShown)
            {
                _saveFailureShown = true;
                _shell.NotifyUser("使い捨てリストを保存できませんでした（ログに記録しました。次の変更でもう一度保存します）");
            }
        }
    }
}
