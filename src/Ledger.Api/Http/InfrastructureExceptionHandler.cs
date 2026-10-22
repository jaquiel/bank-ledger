using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Ledger.Api.Http;

/// <summary>
/// Converte falhas de infraestrutura em respostas honestas e acionáveis.
/// Indisponibilidade vira 503 com Retry-After (o cliente pode repetir com a MESMA Idempotency-Key sem risco
/// de duplicidade). Detalhes internos nunca vão para o corpo da resposta, só para o log.
/// </summary>
public sealed partial class InfrastructureExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<InfrastructureExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var (status, code, detail, retryAfterSeconds) = Classify(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogFailure(logger, code, exception);
        }

        httpContext.Response.StatusCode = status;
        if (retryAfterSeconds is { } seconds)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status switch
                {
                    StatusCodes.Status400BadRequest => "Requisição inválida",
                    StatusCodes.Status503ServiceUnavailable => "Serviço temporariamente indisponível",
                    _ => "Erro interno",
                },
                Detail = detail,
                Type = ProblemResults.TypeBaseUri + code,
                Extensions = { ["code"] = code },
            },
        });
    }

    private static (int Status, string Code, string Detail, int? RetryAfter) Classify(Exception exception) => exception switch
    {
        BadHttpRequestException bad => (bad.StatusCode, "invalid_request", "O corpo ou os parâmetros da requisição são inválidos.", null),
        PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } => (
            StatusCodes.Status503ServiceUnavailable,
            "account_contention",
            "A conta está com alta concorrência no momento. Repita com a mesma Idempotency-Key.",
            1),
        PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } or TimeoutException => (
            StatusCodes.Status503ServiceUnavailable,
            "database_timeout",
            "A operação excedeu o tempo limite. Repita com a mesma Idempotency-Key; não há risco de duplicidade.",
            2),
        // Violação de constraint/trigger é bug, não indisponibilidade: repetir não resolveria.
        PostgresException { IsTransient: false } => (
            StatusCodes.Status500InternalServerError, "internal_error", "Erro inesperado. Informe o traceId ao suporte.", null),
        NpgsqlException => (
            StatusCodes.Status503ServiceUnavailable,
            "database_unavailable",
            "Banco de dados indisponível. Repita com a mesma Idempotency-Key; não há risco de duplicidade.",
            5),
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "Erro inesperado. Informe o traceId ao suporte.", null),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha de infraestrutura ({Code}).")]
    private static partial void LogFailure(ILogger logger, string code, Exception exception);
}
