namespace TaskDeck.Core.Results;

/// <summary>
/// 業務エラー（3階層を超える、名前の重複など）を例外にせず返すための型（設計書 6.1）。
/// Error はそのまま画面に出せる日本語の文。
/// </summary>
public readonly record struct OperationResult(bool Succeeded, string? Error)
{
    public static OperationResult Success() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}

public readonly record struct OperationResult<T>(bool Succeeded, T? Value, string? Error)
{
    public static OperationResult<T> Success(T value) => new(true, value, null);
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}
