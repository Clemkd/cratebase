using System.Diagnostics;
using Cratebase.Admin;
using Cratebase.Auth;
using Cratebase.Core;
using Microsoft.AspNetCore.Http;

namespace Cratebase.Server;

/// <summary>
/// Logs the requests served by the API.
/// </summary>
/// <remarks>
/// <para>
/// Placed <b>around</b> the endpoints, not inside them: a request refused by an access rule, or
/// interrupted by an exception, must leave a trace just as much as a served request — it's even
/// the only one an administrator cares about. Hence capturing the exception, logging it, then
/// rethrowing it intact to the handler that will translate it.
/// </para>
/// <para>
/// Only API requests are retained. The console's static files — a hundred or so per page load —
/// say nothing about how the engine is behaving and would drown the log.
/// </para>
/// </remarks>
public sealed class CratebaseRequestLogMiddleware(
    RequestDelegate next,
    LogStore logs,
    SettingsStore settings,
    CratebaseOptions options)
{
    /// <summary>Maximum length kept for a URL or a header.</summary>
    private const int MaxTextLength = 1024;

    /// <summary>
    /// Parameters whose value is masked in the log.
    /// </summary>
    /// <remarks>
    /// The file token travels in the URL — accepted, since its lifetime is two minutes. Writing it
    /// as-is into a table the console displays would extend its exploitation window by the log's
    /// entire retention period.
    /// </remarks>
    private static readonly string[] SensitiveParameters =
        ["token", "password", "secret", "code", "codeVerifier", "identity"];

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly LogStore _logs = logs ?? throw new ArgumentNullException(nameof(logs));

    private readonly SettingsStore _settings = settings
        ?? throw new ArgumentNullException(nameof(settings));

    private readonly CratebaseOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Processes the request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Concerns(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Write(context, Stopwatch.GetElapsedTime(started), failure);
            throw;
        }

        Write(context, Stopwatch.GetElapsedTime(started), null);
    }

    private bool Concerns(HttpContext context)
    {
        if (!_settings.Current.Logs.Enabled)
        {
            return false;
        }

        var path = context.Request.Path;

        if (!path.StartsWithSegments(_options.ApiPrefix))
        {
            return false;
        }

        // Viewing the log must not fill it: without this exception, every screen refresh would add
        // a line at the top of the page currently being read, and pagination would slide out from
        // under the administrator.
        return !(HttpMethods.IsGet(context.Request.Method) &&
                 path.StartsWithSegments($"{_options.ApiPrefix}/logs"));
    }

    private void Write(HttpContext context, TimeSpan elapsed, Exception? failure)
    {
        var status = failure switch
        {
            CratebaseException business => business.StatusCode,
            not null => StatusCodes.Status500InternalServerError,
            _ => context.Response.StatusCode,
        };

        var level = LogSeverities.ForStatus(status);
        var logging = _settings.Current.Logs;

        if (level < logging.MinLevel)
        {
            return;
        }

        var url = Describe(context.Request);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (failure is not null)
        {
            data["exception"] = failure.GetType().Name;
            data["detail"] = Truncate(failure.Message);
        }

        var caller = context.Items.TryGetValue(HttpCurrentUser.ContextKey, out var value)
            ? value as AuthenticatedRecord
            : null;

        if (caller?.IsSuperuser == true)
        {
            data["superadmin"] = true;
        }

        _logs.Record(new LogEntry
        {
            Level = level,
            Message = $"{context.Request.Method} {url} → {status}",
            Method = context.Request.Method,
            Url = url,
            Status = status,
            Duration = Math.Round(elapsed.TotalMilliseconds, 3),
            AuthCollection = caller?.Collection ?? string.Empty,
            AuthId = caller?.Id.ToString() ?? string.Empty,

            // The origin address is the connection's own, never one announced by a header:
            // "X-Forwarded-For" is declarative, hence forgeable. Behind a load balancer, it's up to
            // the host to install UseForwardedHeaders, which rewrites the connection address after
            // verifying the proxy.
            Ip = logging.LogIp ? context.Connection.RemoteIpAddress?.ToString() ?? string.Empty : string.Empty,
            UserAgent = Truncate(context.Request.Headers.UserAgent.ToString()),
            Referer = Truncate(context.Request.Headers.Referer.ToString()),
            Data = data,
        });
    }

    private static string Describe(HttpRequest request)
    {
        var path = request.Path.ToString();

        if (!request.QueryString.HasValue)
        {
            return Truncate(path);
        }

        var parameters = request.Query.Select(parameter =>
        {
            var masked = SensitiveParameters.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase);

            return $"{parameter.Key}={(masked ? "***" : parameter.Value.ToString())}";
        });

        return Truncate($"{path}?{string.Join('&', parameters)}");
    }

    private static string Truncate(string? value) => value switch
    {
        null => string.Empty,
        { Length: <= MaxTextLength } => value,
        _ => value[..MaxTextLength] + "…",
    };
}
