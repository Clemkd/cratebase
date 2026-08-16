using Cratebase.Core;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Date normalization is the most exposed portability point: SQLite compares instants as strings,
/// PostgreSQL compares them temporally. The two only coincide if the text form is strictly
/// canonical.
/// </summary>
public class TimestampTests
{
    [Fact]
    public void The_canonical_form_has_a_fixed_length()
    {
        var value = Timestamp.Normalize(new DateTimeOffset(2026, 8, 13, 14, 5, 9, 123, TimeSpan.Zero));

        value.ShouldBe("2026-08-13T14:05:09.123Z");
        value.Length.ShouldBe(Timestamp.Length);
    }

    [Fact]
    public void An_instant_with_a_timezone_is_brought_back_to_utc()
    {
        var paris = new DateTimeOffset(2026, 8, 13, 16, 5, 9, 123, TimeSpan.FromHours(2));

        Timestamp.Normalize(paris).ShouldBe("2026-08-13T14:05:09.123Z");
    }

    [Fact]
    public void Leading_zeros_are_kept()
    {
        // Without them, "2026-8-3T4:05" sorts before "2026-12-..." under text comparison: range
        // filters become wrong on SQLite, and only on SQLite.
        var value = Timestamp.Normalize(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero));

        value.ShouldBe("2026-01-02T03:04:05.006Z");
    }

    [Fact]
    public void Lexicographic_order_follows_chronological_order()
    {
        var instants = new[]
        {
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, 999, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, 0, TimeSpan.Zero),
        };

        var canonical = instants.Select(Timestamp.Normalize).ToArray();

        canonical.ShouldBe(canonical.OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("2026-08-13T14:05:09.123Z", "2026-08-13T14:05:09.123Z")]
    [InlineData("2026-08-13T16:05:09.123+02:00", "2026-08-13T14:05:09.123Z")]
    [InlineData("2026-08-13T14:05:09Z", "2026-08-13T14:05:09.000Z")]
    [InlineData("2026-08-13", "2026-08-13T00:00:00.000Z")]
    public void Accepted_client_forms_are_brought_back_to_the_canonical_form(string raw, string expected)
    {
        Timestamp.TryNormalize(raw, out var canonical).ShouldBeTrue();
        canonical.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("2026-13-45")]
    public void Non_temporal_values_are_rejected(string? raw) =>
        Timestamp.TryNormalize(raw, out _).ShouldBeFalse();

    [Fact]
    public void The_write_read_cycle_preserves_the_instant()
    {
        // 2028 is a leap year: February 29 is the edge case worth covering.
        var origin = new DateTimeOffset(2028, 2, 29, 23, 59, 59, 999, TimeSpan.Zero);

        Timestamp.Parse(Timestamp.Normalize(origin)).ShouldBe(origin);
    }

    [Fact]
    public void A_date_with_no_timezone_is_interpreted_as_utc()
    {
        var unspecified = new DateTime(2026, 8, 13, 14, 5, 9, DateTimeKind.Unspecified);

        Timestamp.Normalize(unspecified).ShouldBe("2026-08-13T14:05:09.000Z");
    }
}
