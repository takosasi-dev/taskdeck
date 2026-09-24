using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TaskDeck.App.ViewModels;
using TaskDeck.App.Views.Templates;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Scratch;

namespace TaskDeck.App.Views.Scratch;

/// <summary>使い捨てリストの1行（チェックと1行の文字と字下げ）。書き換えはそのままリストの項目に入る。</summary>
public sealed class ScratchItemViewModel : ObservableObject
{
    /// <summary>1段の字下げ（一覧のサブタスクと同じ幅）。</summary>
    public const double IndentStep = 28;

    private readonly Action _changed;

    internal ScratchItemViewModel(ScratchItem model, Action changed)
    {
        Model = model;
        _changed = changed;
    }

    public ScratchItem Model { get; }

    public string Text
    {
        get => Model.Text;
        set
        {
            if (SetProperty(Model.Text, value ?? "", Model, (m, v) => m.Text = v))
            {
                OnPropertyChanged(nameof(CheckName));
                _changed();
            }
        }
    }

    public bool IsChecked
    {
        get => Model.IsChecked;
        set
        {
            if (SetProperty(Model.IsChecked, value, Model, (m, v) => m.IsChecked = v))
            {
                _changed();
            }
        }
    }

    public int Depth => Model.Depth;

    public double Indent => Model.Depth * IndentStep;

    /// <summary>チェックの読み上げ（「牛乳」をチェック）。</summary>
    public string CheckName => Model.IsBlank ? "空の項目をチェック" : $"「{Model.Text}」をチェック";

    internal void RefreshDepth()
    {
        OnPropertyChanged(nameof(Depth));
        OnPropertyChanged(nameof(Indent));
    }
}

/// <summary>一覧の末尾の「＋ 項目を追加…」の行（中身は持たない目印。入力は ScratchListViewModel.AddText）。</summary>
public sealed class ScratchAddRow
{
    public static readonly ScratchAddRow Instance = new();

    private ScratchAddRow()
    {
    }
}

/// <summary>
/// 使い捨てリストの中身（メイン画面の中央と小窓で同じものを使う）。項目の書き換えはリストのモデルに直接入れ、
/// changed（少し待ってから保存する）を呼ぶ。キー操作（Enter・Tab・Backspace・↑↓）は View が受けてここを呼ぶ。
/// </summary>
public sealed partial class ScratchListViewModel : ObservableObject
{
    /// <summary>「テンプレートとして保存しました」などの知らせを出しておく時間。</summary>
    public const int NoticeMs = 4000;

    private readonly ScratchList _list;
    private readonly ScratchStore _store;
    private readonly ITemplateRepository _templates;
    private readonly Action _changed;
    private readonly ILogger _logger;
    private int _noticeToken;

    public ScratchListViewModel(ScratchList list, ScratchStore store, ITemplateRepository templates, Action changed, ILogger logger)
    {
        _list = list;
        _store = store;
        _templates = templates;
        _changed = changed;
        _logger = logger;
        _nameText = list.Name;
        foreach (var item in list.Items)
        {
            Items.Add(Wrap(item));
        }
    }

    public Guid Id => _list.Id;

    public string Name => _list.Name;

    public ObservableCollection<ScratchItemViewModel> Items { get; } = [];

    /// <summary>名前の欄（打つたびに名前を変える。空の間は元の名前のまま）。</summary>
    [ObservableProperty]
    private string _nameText;

    /// <summary>「＋ 項目を追加…」の欄の文字。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddText))]
    [NotifyCanExecuteChangedFor(nameof(AddFromBoxCommand))]
    private string _addText = "";

    /// <summary>短い知らせ（テンプレートにした・できなかった）。しばらくすると消える。</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsTemplateCommand))]
    private bool _isBusy;

    /// <summary>未チェックが残っているのに捨てるときの確認（View が入れる。OK なら true）。無ければ捨てない。</summary>
    public Func<ConfirmRequest, bool>? ConfirmDiscard { get; set; }

    public bool HasAddText => !string.IsNullOrWhiteSpace(AddText);

    /// <summary>文字のある項目の数（空の行は数えない）。</summary>
    public int TotalCount => Items.Count(i => !i.Model.IsBlank);

    public int RemainingCount => Items.Count(i => !i.Model.IsBlank && !i.IsChecked);

    /// <summary>全部チェックした（「捨てる」を主ボタンにする）。</summary>
    public bool IsAllChecked => TotalCount > 0 && RemainingCount == 0;

    public string Summary => TotalCount == 0
        ? "項目はまだありません"
        : RemainingCount == 0 ? $"{TotalCount} 件すべてチェックしました" : $"残り {RemainingCount} 件（全 {TotalCount} 件）";

    /// <summary>Enter: すぐ下に空の行を足す（子があれば最初の子に）。足した行を返す。</summary>
    public ScratchItemViewModel InsertBelow(ScratchItemViewModel item)
    {
        var index = Items.IndexOf(item);
        var added = Wrap(ScratchOutline.InsertBelow(_list.Items, index));
        Items.Insert(index + 1, added);
        OnChanged();
        return added;
    }

    /// <summary>Tab: 子ごと1段下げる（前の行より2段以上深くしない・3段まで）。</summary>
    public bool Indent(ScratchItemViewModel item) => Shifted(ScratchOutline.Indent(_list.Items, Items.IndexOf(item)));

    /// <summary>Shift+Tab: 子ごと1段上げる。</summary>
    public bool Outdent(ScratchItemViewModel item) => Shifted(ScratchOutline.Outdent(_list.Items, Items.IndexOf(item)));

    /// <summary>空の行で Backspace: 行を消し（子は1段上がる）、次にフォーカスを置く行を返す（前の行・無ければ次の行・無ければ null）。</summary>
    public ScratchItemViewModel? Remove(ScratchItemViewModel item)
    {
        var index = Items.IndexOf(item);
        if (index < 0)
        {
            return null;
        }
        ScratchOutline.Remove(_list.Items, index);
        Items.RemoveAt(index);
        foreach (var row in Items)
        {
            row.RefreshDepth();
        }
        OnChanged();
        return index > 0 ? Items[index - 1] : Items.FirstOrDefault();
    }

    /// <summary>Ctrl+Enter: チェックを切り替える（Tab が字下げなので、キーボードだけでもチェックできるように）。</summary>
    public void Toggle(ScratchItemViewModel item) => item.IsChecked = !item.IsChecked;

    /// <summary>名前の欄を離れた・Enter: 整えた名前を欄に戻す（空にしていたら元の名前に戻る）。</summary>
    public void CommitName() => NameText = _list.Name;

    partial void OnNameTextChanged(string value)
    {
        if (_store.Rename(_list.Id, value))
        {
            OnPropertyChanged(nameof(Name));
            _changed();
        }
    }

    /// <summary>「＋ 項目を追加…」に打った文字を末尾（いちばん上の段）に足す。欄は空にしてそのまま打ち続けられる。</summary>
    [RelayCommand(CanExecute = nameof(HasAddText))]
    private void AddFromBox()
    {
        var text = AddText.Trim();
        if (text.Length == 0)
        {
            return;
        }
        var item = new ScratchItem { Text = text };
        _list.Items.Add(item);
        Items.Add(Wrap(item));
        AddText = "";
        OnChanged();
    }

    /// <summary>今の項目（題名と階層）をそのまま新しいテンプレートにする。チェックは持ち込まない。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAsTemplate))]
    private async Task SaveAsTemplateAsync()
    {
        var items = ScratchTemplates.ToTemplateItems(_list.Items);
        if (items.Count == 0)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var saved = await _templates.SaveAsync(new TaskTemplate { Name = _list.Name, IconKey = "IconList" }, items);
            _logger.LogInformation("使い捨てリスト {ListId} をテンプレート {TemplateId} にしました（項目 {Count} 件）", _list.Id, saved.Id, items.Count);
            ShowNotice($"テンプレート「{saved.Name}」として保存しました");
        }
        catch (Exception ex) when (TemplatesViewModel.IsDataError(ex))
        {
            _logger.LogError(ex, "使い捨てリスト {ListId} をテンプレートにできませんでした", _list.Id);
            ShowNotice("テンプレートにできませんでした（ログに記録しました）");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveAsTemplate() => TotalCount > 0 && !IsBusy;

    /// <summary>リストを捨てる。未チェックが残っているときだけ1回確認する（取り消しは無い）。</summary>
    [RelayCommand]
    private void Discard()
    {
        if (RemainingCount > 0)
        {
            var request = new ConfirmRequest(
                "使い捨てリストを捨てる",
                $"「{_list.Name}」にはチェックしていない項目が {RemainingCount} 件あります。\n捨てたリストは元に戻せません。捨てますか？");
            if (ConfirmDiscard?.Invoke(request) != true)
            {
                return;
            }
        }
        if (_store.Discard(_list.Id))
        {
            _logger.LogInformation("使い捨てリスト {ListId} を捨てました", _list.Id);
            _changed();
        }
    }

    private ScratchItemViewModel Wrap(ScratchItem item) => new(item, OnChanged);

    private bool Shifted(bool changed)
    {
        if (changed)
        {
            foreach (var row in Items)
            {
                row.RefreshDepth();
            }
            _changed();
        }
        return changed;
    }

    private void OnChanged()
    {
        _changed();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(IsAllChecked));
        OnPropertyChanged(nameof(Summary));
        SaveAsTemplateCommand.NotifyCanExecuteChanged();
    }

    private async void ShowNotice(string message)
    {
        var token = ++_noticeToken;
        Notice = message;
        await Task.Delay(NoticeMs);
        if (token == _noticeToken)
        {
            Notice = null;
        }
    }
}
