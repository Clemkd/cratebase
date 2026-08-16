using Cratebase.Admin;
using Cratebase.Core;
using Cratebase.Server;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Settings are written through a partial PATCH. The rule that matters: whatever the payload
/// doesn't mention doesn't move — otherwise a screen that only sends the instance name would reset
/// retention and address collection to their defaults, silently.
/// </summary>
public class AdminSettingsTests
{
    private static readonly AppSettings Current = new()
    {
        AppName = "Production",
        AppUrl = "https://example.org",
        Logs = new LogSettings
        {
            Enabled = true,
            RetentionDays = 30,
            MinLevel = LogSeverity.Warning,
            LogIp = false,
        },
    };

    [Fact]
    public void An_empty_payload_changes_nothing()
    {
        var result = new SettingsRequest().Apply(Current);

        result.ShouldBe(Current);
    }

    [Fact]
    public void A_supplied_field_does_not_reset_the_others()
    {
        var result = new SettingsRequest { AppName = "Staging" }.Apply(Current);

        result.AppName.ShouldBe("Staging");
        result.AppUrl.ShouldBe("https://example.org");
        result.Logs.RetentionDays.ShouldBe(30);
        result.Logs.MinLevel.ShouldBe(LogSeverity.Warning);
        result.Logs.LogIp.ShouldBeFalse();
    }

    [Fact]
    public void A_partial_sub_object_does_not_reset_its_siblings()
    {
        var result = new SettingsRequest
        {
            Logs = new LogSettingsRequest { RetentionDays = 3 },
        }.Apply(Current);

        result.Logs.RetentionDays.ShouldBe(3);
        result.Logs.Enabled.ShouldBeTrue();
        result.Logs.MinLevel.ShouldBe(LogSeverity.Warning);
        result.Logs.LogIp.ShouldBeFalse();
    }

    [Fact]
    public void A_boolean_set_to_false_is_correctly_applied()
    {
        // The classic partial-merge trap: `false ?? default` must yield false, not fall back to
        // the current value because the test would be on truthiness.
        var result = new SettingsRequest
        {
            Logs = new LogSettingsRequest { Enabled = false },
        }.Apply(Current);

        result.Logs.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void The_name_is_required()
    {
        var failure = Should.Throw<CratebaseValidationException>(
            () => (Current with { AppName = "   " }).Validated());

        failure.Errors.ShouldContainKey("appName");
    }

    [Fact]
    public void Retention_stays_within_bounds()
    {
        var failure = Should.Throw<CratebaseValidationException>(
            () => (Current with { Logs = Current.Logs with { RetentionDays = 900 } }).Validated());

        failure.Errors.ShouldContainKey("logs.retentionDays");

        // Zero is valid: it means unlimited retention.
        Should.NotThrow(() => (Current with { Logs = Current.Logs with { RetentionDays = 0 } }).Validated());
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("ftp://example.org")]
    [InlineData("javascript:alert(1)")]
    public void A_public_url_must_be_absolute_and_http(string url)
    {
        var failure = Should.Throw<CratebaseValidationException>(
            () => (Current with { AppUrl = url }).Validated());

        failure.Errors.ShouldContainKey("appUrl");
    }

    [Fact]
    public void The_name_and_url_are_normalized()
    {
        var result = (Current with { AppName = "  Production  ", AppUrl = "https://example.org/" })
            .Validated();

        result.AppName.ShouldBe("Production");

        // The trailing slash is stripped: without this, a URL built by concatenation would produce
        // "https://example.org//api".
        result.AppUrl.ShouldBe("https://example.org");
    }

    [Fact]
    public void An_empty_url_stays_accepted()
    {
        Should.NotThrow(() => (Current with { AppUrl = "" }).Validated());
    }
}

/// <summary>Mapping between HTTP status and severity, and bucket splitting.</summary>
public class LogSeverityTests
{
    [Theory]
    [InlineData(200, LogSeverity.Info)]
    [InlineData(304, LogSeverity.Info)]
    [InlineData(400, LogSeverity.Warning)]
    [InlineData(401, LogSeverity.Warning)]
    [InlineData(404, LogSeverity.Warning)]
    [InlineData(500, LogSeverity.Error)]
    [InlineData(503, LogSeverity.Error)]
    public void A_status_yields_a_severity(int status, LogSeverity expected)
    {
        LogSeverities.ForStatus(status).ShouldBe(expected);
    }

    [Fact]
    public void The_level_filter_is_inclusive()
    {
        LogSeverities.AtLeast(LogSeverity.Warning)
            .ShouldBe([LogSeverity.Warning, LogSeverity.Error]);

        LogSeverities.AtLeast(LogSeverity.Debug).Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("2026-08-13", "2026-08-13T00:00:00.000Z")]
    [InlineData("2026-08-13T14", "2026-08-13T14:00:00.000Z")]
    [InlineData("2026-08-13T14:05", "2026-08-13T14:05:00.000Z")]
    public void A_bucket_reads_back_as_an_instant(string bucket, string expected)
    {
        Timestamp.Normalize(LogBuckets.ToInstant(bucket)).ShouldBe(expected);
    }
}
