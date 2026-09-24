using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using IcalRule = Ical.Net.DataTypes.RecurrenceRule;

namespace TaskDeck.Core.Services.Recurrence;

/// <summary>
/// RRULE（RFC 5545）を <b>ローカルの壁時計時刻</b>で評価する（Ical.Net 5.2.3）。
/// タイムゾーンを持たない浮動時刻（Kind=Unspecified）で数えるので、UTC への変換は呼び出し側（IClock）が行う。
/// 壊れた RRULE は例外にせず「発生なし」として扱う（設定画面や DB から壊れた値が来てもアプリを落とさない）。
/// </summary>
internal static class RruleEvaluator
{
    /// <summary>1回の列挙で取り出す上限。壊れたルールで回り続けないための保険。</summary>
    public const int MaxEnumerated = 3000;

    /// <summary>
    /// referenceLocal 以降の発生日時（referenceLocal 自身が条件に合えばそれも含む）。遅延列挙。
    /// hasTime=false なら日付だけで数え、結果の時刻は 0:00。
    /// </summary>
    public static IEnumerable<DateTime> From(string rrule, DateTime referenceLocal, bool hasTime)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            // Ical.Net は空文字を FREQ=YEARLY として受けてしまうので、ここで止める
            yield break;
        }

        IEnumerator<Period> periods;
        try
        {
            var rule = new IcalRule(rrule);
            var reference = new CalDateTime(DateTime.SpecifyKind(referenceLocal, DateTimeKind.Unspecified), hasTime);
            periods = new RecurrencePatternEvaluator(rule).Evaluate(reference, reference, null).GetEnumerator();
        }
        catch (ArgumentException)
        {
            // RRULE の書式が壊れている（"GARBAGE"、BYMONTHDAY=99 など）
            yield break;
        }
        catch (OverflowException)
        {
            // INTERVAL が Int32 に収まらない
            yield break;
        }

        using (periods)
        {
            for (var i = 0; i < MaxEnumerated; i++)
            {
                DateTime value;
                try
                {
                    if (!periods.MoveNext())
                    {
                        yield break;
                    }
                    value = periods.Current.StartTime.Value;
                }
                catch (EvaluationException)
                {
                    // 一生来ない条件（2月30日など）や評価の打ち切り
                    yield break;
                }
                yield return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            }
        }
    }

    /// <summary>referenceLocal より後の発生日時（referenceLocal 自身は含まない）。</summary>
    public static IEnumerable<DateTime> After(string rrule, DateTime referenceLocal, bool hasTime) =>
        From(rrule, referenceLocal, hasTime).Where(d => d > referenceLocal);
}
