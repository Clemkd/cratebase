using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Filter language macros, resolved at compile time.
/// </summary>
/// <remarks>
/// <para>
/// They become <b>parameters</b>, not SQL function calls. This is deliberate, and it's the point
/// that sets Cratebase apart from PocketBase on portability: PocketBase translates its macros into
/// <c>strftime()</c>, hence into SQLite, hence into a dead end. Resolved on the application side,
/// they produce exactly the same value regardless of engine.
/// </para>
/// <para>
/// Desirable side effect: the value is frozen for the whole duration of a request, so two
/// comparisons against <c>@now</c> in the same expression can never land on opposite sides of a
/// millisecond boundary.
/// </para>
/// </remarks>
public static class DateMacros
{
    /// <summary>
    /// Resolves a macro.
    /// </summary>
    /// <param name="name">Macro name, including the leading @.</param>
    /// <param name="now">Request's reference instant.</param>
    /// <param name="value">Resolved value: canonical string, or a number for components.</param>
    /// <returns><see langword="false"/> if the macro is unknown.</returns>
    public static bool TryResolve(string name, DateTimeOffset now, out object? value)
    {
        var utc = now.ToUniversalTime();

        value = name switch
        {
            "@now" => Timestamp.Normalize(utc),
            "@yesterday" => Timestamp.Normalize(utc.AddDays(-1)),
            "@tomorrow" => Timestamp.Normalize(utc.AddDays(1)),

            "@todayStart" => Timestamp.Normalize(StartOfDay(utc)),
            "@todayEnd" => Timestamp.Normalize(StartOfDay(utc).AddDays(1).AddMilliseconds(-1)),

            "@monthStart" => Timestamp.Normalize(StartOfMonth(utc)),
            "@monthEnd" => Timestamp.Normalize(StartOfMonth(utc).AddMonths(1).AddMilliseconds(-1)),

            "@yearStart" => Timestamp.Normalize(StartOfYear(utc)),
            "@yearEnd" => Timestamp.Normalize(StartOfYear(utc).AddYears(1).AddMilliseconds(-1)),

            "@second" => (double)utc.Second,
            "@minute" => (double)utc.Minute,
            "@hour" => (double)utc.Hour,
            "@day" => (double)utc.Day,
            "@month" => (double)utc.Month,
            "@year" => (double)utc.Year,

            _ => null,
        };

        return value is not null;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset utc) =>
        new(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset StartOfMonth(DateTimeOffset utc) =>
        new(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset StartOfYear(DateTimeOffset utc) =>
        new(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
