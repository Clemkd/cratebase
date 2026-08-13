using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Macros du langage de filtre, résolues à la compilation.
/// </summary>
/// <remarks>
/// <para>
/// Elles deviennent des <b>paramètres</b>, pas des appels de fonction SQL. C'est délibéré et c'est
/// le point qui distingue Cratebase de PocketBase sur la portabilité : PocketBase traduit ses
/// macros en <c>strftime()</c>, donc en SQLite, donc en cul-de-sac. Résolues côté application,
/// elles produisent exactement la même valeur quel que soit le moteur.
/// </para>
/// <para>
/// Effet de bord souhaitable : la valeur est figée pour toute la durée d'une requête, donc deux
/// comparaisons à <c>@now</c> dans la même expression ne peuvent pas tomber de part et d'autre
/// d'une frontière de milliseconde.
/// </para>
/// </remarks>
public static class DateMacros
{
    /// <summary>
    /// Résout une macro.
    /// </summary>
    /// <param name="name">Nom de la macro, arobase comprise.</param>
    /// <param name="now">Instant de référence de la requête.</param>
    /// <param name="value">Valeur résolue : chaîne canonique, ou nombre pour les composantes.</param>
    /// <returns><see langword="false"/> si la macro est inconnue.</returns>
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
