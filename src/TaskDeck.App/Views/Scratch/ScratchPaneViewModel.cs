using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskDeck.App.Views.Scratch;

/// <summary>
/// メイン画面の中央に出す使い捨てリスト（ビューが ViewKind.Scratch のとき）。
/// そのリストを小窓に出している間は中身の代わりに「小窓で表示中［戻す］」を出す。
/// </summary>
public sealed partial class ScratchPaneViewModel : ObservableObject
{
    private readonly ScratchPresenter _presenter;

    public ScratchPaneViewModel(ScratchPresenter presenter)
    {
        _presenter = presenter;
        presenter.WindowListChanged += (_, _) => OnWindowListChanged();
    }

    [ObservableProperty]
    private ScratchListViewModel? _list;

    /// <summary>このリストを小窓に出している。</summary>
    [ObservableProperty]
    private bool _isDetached;

    /// <summary>「小窓で開く」で小窓に移した（メイン画面は一覧に戻る）。</summary>
    public event EventHandler? MovedToWindow;

    /// <summary>ビューを選んだ。リストが見つからなければ空にする。</summary>
    public void Show(Guid listId)
    {
        List = _presenter.Store.Find(listId) is { } list ? _presenter.CreateListViewModel(list) : null;
        IsDetached = _presenter.WindowListId == listId;
        if (List is not null && !IsDetached)
        {
            _presenter.NoteShownIn(ScratchPlace.Main);
        }
    }

    /// <summary>別のビューに移った。</summary>
    public void Clear()
    {
        List = null;
        IsDetached = false;
    }

    /// <summary>サイドバーの「＋」: 新しいリストを最後に使った方（メイン画面か小窓か）で開く。</summary>
    public void CreateAndOpen() => _presenter.Open(_presenter.CreateNew().Id);

    /// <summary>見出しの「小窓で開く」。</summary>
    [RelayCommand]
    private void Detach()
    {
        if (List is not { } list)
        {
            return;
        }
        _presenter.ShowInWindow(list.Id);
        MovedToWindow?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「小窓で表示中［戻す］」の「戻す」。</summary>
    [RelayCommand]
    private void Attach()
    {
        if (List is { } list)
        {
            _presenter.ShowInMain(list.Id);
        }
    }

    private void OnWindowListChanged()
    {
        if (List is not { } list)
        {
            return;
        }
        var detached = _presenter.WindowListId == list.Id;
        if (IsDetached && !detached)
        {
            if (_presenter.Store.Find(list.Id) is not { } model)
            {
                return;   // 小窓で捨てられた（メイン画面が一覧へ戻す）
            }
            // 小窓で書き換えた分も出るように作り直す
            List = _presenter.CreateListViewModel(model);
        }
        IsDetached = detached;
    }
}
