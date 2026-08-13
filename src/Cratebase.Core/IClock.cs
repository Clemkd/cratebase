namespace Cratebase.Core;

/// <summary>
/// Source de temps. Injectée partout plutôt que d'appeler <see cref="DateTimeOffset.UtcNow"/>,
/// pour que les macros de date du langage de filtre (<c>@now</c>, <c>@todayStart</c>…) soient
/// testables sur les bascules d'heure, de jour et d'année.
/// </summary>
public interface IClock
{
    /// <summary>Instant courant, toujours en UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Horloge système.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Instance partagée.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
