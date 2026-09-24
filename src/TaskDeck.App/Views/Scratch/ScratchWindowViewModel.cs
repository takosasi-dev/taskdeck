using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskDeck.Core.Scratch;

namespace TaskDeck.App.Views.Scratch;

/// <summary>小窓の見出しの「名前 ▾」で選べるリスト1つ。</summary>
public sealed record ScratchListChoice(Guid Id, string Name, bool IsCurrent)
{
    /// <summary>UI Automation の項目名。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 使い捨てリストの小窓。1つのリストを出し、見出しの ▾ で他のリストに切り替える。
/// 出しているリストを捨てたら窓を閉じる。「メイン画面に戻す」でメイン画面の中央に移す。
/// </summary>
public sealed partial class ScratchWindowViewModel : ObservableObject
{
    private readonly ScratchPresenter _presenter;

    public ScratchWindowViewModel(ScratchPresenter presenter)
    {
        _presenter = presenter;
        _isPinned = presenter.WindowState.Pinned;
        presenter.Store.Changed += OnStoreChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ScratchListViewModel? _list;

    /// <summary>常に手前に固定する（ピン）。</summary>
    [ObservableProperty]
    private bool _isPinned;

    public ObservableCollection<ScratchListChoice> Choices { get; } = [];

    /// <summary>タスクバーに出る名前。</summary>
    public string WindowTitle => List is { } list ? $"{list.Name}（使い捨て）" : "使い捨てリスト";

    public event EventHandler? CloseRequested;

    /// <summary>このリストを出す（出しているものと同じなら作り直さない）。</summary>
    public void Show(Guid listId)
    {
        if (_presenter.Store.Find(listId) is not { } list)
        {
            return;
        }
        if (List?.Id != listId)
        {
            List = _presenter.CreateListViewModel(list);
        }
        _presenter.SetWindowList(listId);
        RefreshChoices();
    }

    /// <summary>小窓が閉じるときの位置と大きさを覚える。</summary>
    public void RememberPlacement(double left, double top, double width, double height)
    {
        var state = _presenter.WindowState;
        state.Left = left;
        state.Top = top;
        state.Width = width;
        state.Height = height;
        _presenter.SaveWindowState();
    }

    /// <summary>窓が閉じた（リストの変更を追うのをやめる）。</summary>
    public void Release() => _presenter.Store.Changed -= OnStoreChanged;

    partial void OnIsPinnedChanged(bool value)
    {
        _presenter.WindowState.Pinned = value;
        _presenter.SaveWindowState();
    }

    [RelayCommand]
    private void SwitchTo(ScratchListChoice? choice)
    {
        if (choice is not null)
        {
            Show(choice.Id);
        }
    }

    /// <summary>新しいリストを作って、この小窓に出す。</summary>
    [RelayCommand]
    private void NewList() => _presenter.ShowInWindow(_presenter.CreateNew().Id);

    [RelayCommand]
    private void DockToMain()
    {
        if (List is { } list)
        {
            _presenter.ShowInMain(list.Id);
        }
    }

    private void OnStoreChanged(object? sender, ScratchChangedEventArgs e)
    {
        if (e.Change == ScratchChange.Discarded && e.ListId == List?.Id)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        RefreshChoices();
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void RefreshChoices()
    {
        Choices.Clear();
        foreach (var list in _presenter.Store.Lists)
        {
            Choices.Add(new ScratchListChoice(list.Id, list.Name, list.Id == List?.Id));
        }
    }
}
