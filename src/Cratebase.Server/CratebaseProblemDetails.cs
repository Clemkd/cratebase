using Cratebase.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cratebase.Server;

/// <summary>
/// Translates business exceptions into <c>ProblemDetails</c> responses.
/// </summary>
/// <remarks>
/// The status is carried by the exception itself: the translation has nothing to guess, and a new
/// business exception can't be forgotten here.
/// </remarks>
public sealed partial class CratebaseExceptionHandler(ILogger<CratebaseExceptionHandler> logger)
    : IExceptionHandler
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Access denied on {Path}: {Reason}")]
    private static partial void LogAccessDenied(ILogger logger, string path, string reason);

    private readonly ILogger<CratebaseExceptionHandler> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not CratebaseException business)
        {
            return false;
        }

        var path = httpContext.Request.Path.ToString();

        var problem = new ProblemDetails
        {
            Status = business.StatusCode,
            Title = TitleFor(business.StatusCode),
            Detail = business.Message,
            Instance = path,
        };

        if (exception is CratebaseValidationException validation)
        {
            problem.Extensions["errors"] = validation.Errors;
        }

        // An access refusal is logged, an input error isn't: the former can signal an attempt, the
        // latter is the normal operation of a public API.
        if (business.StatusCode is 401 or 403)
        {
            LogAccessDenied(_logger, path, business.Message);
        }

        httpContext.Response.StatusCode = business.StatusCode;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Invalid request",
        401 => "Authentication required",
        403 => "Insufficient permission",
        404 => "Resource not found",
        409 => "Conflict",
        _ => "Error",
    };
}
