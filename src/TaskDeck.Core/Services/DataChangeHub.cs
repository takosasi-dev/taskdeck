namespace TaskDeck.Core.Services;

[Flags]
public enum DataChangeKind
{
    None = 0,
    Tasks = 1,
    Projects = 2,
    Tags = 4,
    Templates = 8,
    /// <summary>祝日・天気などのキャッシュ。</summary>
    ExternalCache = 16,
    All = Tasks | Projects | Tags | Templates | ExternalCache,
}

public sealed class DataChangedEventArgs(DataChangeKind kinds, IReadOnlyCollection<Guid> taskIds) : EventArgs
{
    public DataChangeKind Kinds { get; } = kinds;

    /// <summary>変わったタスクの Id（分かる範囲。空なら「どれか」）。</summary>
    public IReadOnlyCollection<Guid> TaskIds { get; } = taskIds;
}

/// <summary>
/// データが変わったことの通知。リポジトリがコミット後に必ず Publish する。
/// 一覧・サイドバー・トレイ・通知はこれを購読して読み直す。発火は書き込んだスレッドで起きるので、UI は Dispatcher に戻すこと。
/// </summary>
public sealed class DataChangeHub
{
    public event EventHandler<DataChangedEventArgs>? Changed;

    public void Publish(DataChangeKind kinds, IReadOnlyCollection<Guid>? taskIds = null) =>
        Changed?.Invoke(this, new DataChangedEventArgs(kinds, taskIds ?? []));
}
