using Cratebase.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cratebase.Server;

/// <summary>
/// Traduit les exceptions métier en réponses <c>ProblemDetails</c>.
/// </summary>
/// <remarks>
/// Le statut est porté par l'exception elle-même : la traduction n'a rien à deviner, et une
/// nouvelle exception métier ne peut pas être oubliée ici.
/// </remarks>
public sealed partial class CratebaseExceptionHandler(ILogger<CratebaseExceptionHandler> logger)
    : IExceptionHandler
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Accès refusé sur {Path} : {Reason}")]
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

        // Un refus d'accès est journalisé, pas une erreur d'entrée : le premier peut signaler une
        // tentative, le second est le fonctionnement normal d'une API publique.
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
        400 => "Requête invalide",
        401 => "Authentification requise",
        403 => "Droit insuffisant",
        404 => "Ressource introuvable",
        409 => "Conflit",
        _ => "Erreur",
    };
}
