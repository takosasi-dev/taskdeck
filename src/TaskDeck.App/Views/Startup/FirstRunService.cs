using System.Globalization;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Startup;

/// <summary>
/// 初回起動の3画面（S-15）を出すかどうかと、終えた記録（AppStateKeys.FirstRunCompleted）。
/// 開発用フォルダでは TASKDECK_DEV_FIRSTRUN=1 のときだけ出す（終えた後でも出す。他の確認作業の起動を止めないため）。
/// 2・3 ならその画面から始める（開発用フォルダではホットキーを登録しないので、②から先へは進めないため）。
/// </summary>
public sealed class FirstRunService(IAppStateRepository state, IClock clock, AppPaths paths)
{
    public const string DevVariable = "TASKDECK_DEV_FIRSTRUN";

    /// <summary>開発用フォルダで始める画面（TASKDECK_DEV_FIRSTRUN=1〜3）。本番と、指定が無いときは null。</summary>
    public FirstRunStep? DevStartStep =>
        paths.IsDevelopment && int.TryParse(Environment.GetEnvironmentVariable(DevVariable), out var step) && step is >= 1 and <= 3
            ? (FirstRunStep)step
            : null;

    public async Task<bool> ShouldShowAsync(CancellationToken ct = default)
    {
        if (paths.IsDevelopment)
        {
            return DevStartStep is not null;
        }
        return await state.GetAsync(AppStateKeys.FirstRunCompleted, ct) is null;
    }

    /// <summary>終えた（最後まで進んだ・スキップ・× のどれでも。次からは出さない）。</summary>
    public Task MarkCompletedAsync(CancellationToken ct = default) =>
        state.SetAsync(AppStateKeys.FirstRunCompleted, clock.LocalToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
}
