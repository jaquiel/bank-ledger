using Ledger.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Api.Http;

/// <summary>Tradução única de erros de negócio para HTTP (RFC 9457 - Problem Details).</summary>
public static class ProblemResults
{
    public const string TypeBaseUri = "https://ledger.example/errors/";

    public static ProblemHttpResult ToProblem(this DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (status, title) = error.Kind switch
        {
            ErrorKind.Validation => (StatusCodes.Status400BadRequest, "Requisição inválida"),
            ErrorKind.NotFound => (StatusCodes.Status404NotFound, "Recurso não encontrado"),
            ErrorKind.Conflict => (StatusCodes.Status409Conflict, "Conflito"),
            _ => (StatusCodes.Status422UnprocessableEntity, "Operação recusada pela regra de negócio"),
        };

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: status,
            title: title,
            type: TypeBaseUri + error.Code,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
