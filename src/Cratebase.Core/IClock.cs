namespace Cratebase.Core;

/// <summary>
/// Source of time. Injected everywhere rather than calling <see cref="DateTimeOffset.UtcNow"/>
/// directly, so the filter language's date macros (<c>@now</c>, <c>@todayStart</c>…) can be tested
/// across hour, day, and year boundaries.
/// </summary>
public interface IClock
{
    /// <summary>Current instant, always in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>System clock.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
