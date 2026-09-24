using TaskDeck.App.ViewModels;

namespace TaskDeck.Tests.App.ViewModels;

/// <summary>取り消しトースト（同時に1件・元に戻す・閉じる）。</summary>
public class ToastViewModelTests
{
    [Fact]
    public void Show_続けて出す_内容を差し替えて1件だけ出す()
    {
        var toast = new ToastViewModel();

        toast.Show("「A」を削除しました", "元に戻す", "IconTrash");
        toast.Show("完了しました・次回は 9月29日（火）", "元に戻す", "IconCheck");

        Assert.True(toast.IsOpen);
        Assert.Equal("完了しました・次回は 9月29日（火）", toast.Message);
        Assert.Equal("IconCheck", toast.IconKey);
    }

    [Fact]
    public void InvokeActionCommand_閉じてから元に戻すを知らせる()
    {
        var toast = new ToastViewModel();
        var invoked = 0;
        toast.ActionInvoked += (_, _) => invoked++;
        toast.Show("「A」を削除しました", "元に戻す", "IconTrash");

        toast.InvokeActionCommand.Execute(null);

        Assert.False(toast.IsOpen);
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void Show_操作なし_元に戻すを出さない()
    {
        var toast = new ToastViewModel();

        toast.Show("問題が発生しました", null, "IconWarning");

        Assert.False(toast.HasAction);
    }
}
