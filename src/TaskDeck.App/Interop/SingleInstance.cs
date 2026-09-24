using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TaskDeck.App.Interop;

/// <summary>
/// 多重起動防止（F-095）。Mutex で1つに絞り、2つ目の起動は名前付きパイプで1つ目に「前に出て」と伝えて終わる。
/// 名前は AppPaths.InstanceKey（開発時はデータフォルダごとに別）＋ユーザー名。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>最初の1つなら SingleInstance を返す。既に起動していれば null。</summary>
    public static SingleInstance? TryAcquire(string instanceKey)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{instanceKey}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, PipeName(instanceKey));
    }

    /// <summary>起動済みのインスタンスに引数を送る。届いたら true。</summary>
    public static bool SignalExisting(string instanceKey, IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(instanceKey), PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', args));
            writer.Flush();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>2つ目の起動からの合図を待ち受ける（バックグラウンド）。onActivate はパイプのスレッドで呼ばれる。</summary>
    public void StartListening(Action<string[]> onActivate, ILogger logger)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    onActivate(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "起動済みインスタンスへの合図を受け取れませんでした");
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _mutex.Dispose();
    }

    private static string PipeName(string instanceKey) => $"{instanceKey}-{Environment.UserName}";
}
