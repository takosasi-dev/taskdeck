using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskDeck.App.Services;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Foundation;

public class ShellServiceTests
{
    private readonly ShellService _shell = new(Substitute.For<IServiceProvider>(), FixedClock.AtLocal(2026, 9, 22, 10, 0), NullLogger<ShellService>.Instance);

    [Fact]
    public void NotifyUser_BeforeSubscribed_DeliversLatestThreeAsOneLineOnSubscribe()
    {
        foreach (var message in new[] { "1", "2", "3", "4" })
        {
            _shell.NotifyUser(message);
        }
        var received = new List<string>();

        _shell.UserNotice += (_, message) => received.Add(message);
        _shell.NotifyUser("5");

        Assert.Equal(["2　3　4", "5"], received);
    }

    [Fact]
    public void NotifyUser_Subscribed_DeliversAtOnceAndKeepsNothing()
    {
        var first = new List<string>();
        _shell.UserNotice += (_, message) => first.Add(message);
        _shell.NotifyUser("知らせ");
        var second = new List<string>();

        _shell.UserNotice += (_, message) => second.Add(message);

        Assert.Equal(["知らせ"], first);
        Assert.Empty(second);
    }
}
