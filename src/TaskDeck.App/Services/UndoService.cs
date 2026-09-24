using Microsoft.Extensions.Logging;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;

namespace TaskDeck.App.Services;

/// <summary>取り消しトースト（S-08）を出してほしいときの通知。</summary>
public sealed class UndoToastEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

/// <summary>
/// 取り消し（F-141 / F-142）。書き込みの結果を Record で積み、Ctrl+Z やトーストの「元に戻す」で UndoLatestAsync を呼ぶ。
/// トーストを出すか（削除・一括・繰り返しの完了など）は呼び出し側が決める（UI 設計書 13.1）。
/// </summary>
public sealed class UndoService(ITaskRepository repository, UndoStack stack, ILogger<UndoService> logger)
{
    /// <summary>トーストを出してほしいとき（UI スレッドとは限らない）。</summary>
    public event EventHandler<UndoToastEventArgs>? ToastRequested;

    public UndoStack Stack => stack;

    public bool CanUndo => stack.Count > 0;

    public void Record(TaskMutationResult result, string label, UndoKind kind, bool showToast)
    {
        if (result.Changes.IsEmpty)
        {
            return;
        }
        stack.Push(new UndoEntry(label, result.Changes, kind));
        if (showToast)
        {
            ToastRequested?.Invoke(this, new UndoToastEventArgs(label));
        }
    }

    /// <summary>直前の操作を取り消す。取り消すものが無ければ false。</summary>
    public async Task<bool> UndoLatestAsync(CancellationToken ct = default)
    {
        var entry = stack.Pop();
        if (entry is null)
        {
            return false;
        }
        await repository.ApplyUndoAsync(entry.Changes, ct);
        logger.LogInformation("取り消しました: {Kind}（{Count}件）", entry.Kind, entry.Changes.TasksBefore.Count + entry.Changes.CreatedTaskIds.Count);
        return true;
    }
}
