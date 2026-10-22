using System.Diagnostics;
using Ledger.Api.Auth;
using Ledger.Api.Http;
using Ledger.Api.Observability;
using Ledger.Application;
using Ledger.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ledger.Api.Endpoints;

public static class LedgerEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/v1/accounts")
            .WithTags("Accounts")
            .RequireRateLimiting(RateLimitingSetup.PolicyName)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        accounts.MapPost("/", CreateAccountAsync)
            .RequireAuthorization(Policies.Write)
            .WithSummary("Abre uma conta (saldo zero).");

        accounts.MapGet("/{accountId:guid}", GetAccountAsync)
            .RequireAuthorization(Policies.Read)
            .WithSummary("Dados e saldo corrente da conta.");

        accounts.MapGet("/{accountId:guid}/balance", GetBalanceAsync)
            .RequireAuthorization(Policies.Read)
            .WithSummary("Posição consolidada: atual ou no instante 'at' (ISO 8601).");

        accounts.MapPost("/{accountId:guid}/movements", RegisterMovementAsync)
            .RequireAuthorization(Policies.Write)
            .WithSummary("Registra crédito ou débito. Exige o header Idempotency-Key.");

        accounts.MapGet("/{accountId:guid}/movements", GetStatementAsync)
            .RequireAuthorization(Policies.Read)
            .WithSummary("Extrato paginado por cursor, do mais recente para o mais antigo.");

        accounts.MapGet("/{accountId:guid}/movements/{entryId:guid}", GetEntryAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetEntry")
            .WithSummary("Detalhe de um lançamento.");

        accounts.MapPost("/{accountId:guid}/movements/{entryId:guid}/reversal", ReverseEntryAsync)
            .RequireAuthorization(Policies.Write)
            .WithSummary("Estorna um lançamento com um lançamento compensatório. Exige Idempotency-Key.");

        return app;
    }

    /// <summary>Emissão de token local para avaliar a API sem um provedor OIDC. Mapeado apenas em Development.</summary>
    public static IEndpointRouteBuilder MapDevTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/token", (DevTokenRequest request, IOptions<LedgerAuthOptions> auth, TimeProvider time) =>
            {
                var lifetime = TimeSpan.FromHours(1);
                var scopes = request.Scopes is { Count: > 0 } requested ? requested : [Scopes.Read, Scopes.Write];
                var token = DevTokenIssuer.Issue(auth.Value, request.ClientId ?? "dev-client", scopes, lifetime, time);
                return TypedResults.Ok(new DevTokenResponse(token, "Bearer", (int)lifetime.TotalSeconds));
            })
            .AllowAnonymous()
            .WithTags("Development")
            .WithSummary("[Somente Development] Emite um JWT de teste.");
        return app;
    }

    private static async Task<Results<Created<AccountResponse>, ProblemHttpResult>> CreateAccountAsync(
        CreateAccountRequest? request, LedgerService service, CancellationToken cancellationToken)
    {
        var result = await service.CreateAccountAsync(request?.OverdraftLimit, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var account = AccountResponse.From(result.Value);
        return TypedResults.Created($"/v1/accounts/{account.Id}", account);
    }

    private static async Task<Results<Ok<AccountResponse>, ProblemHttpResult>> GetAccountAsync(
        Guid accountId, LedgerService service, CancellationToken cancellationToken)
    {
        var result = await service.GetAccountAsync(accountId, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(AccountResponse.From(result.Value)) : result.Error!.ToProblem();
    }

    private static async Task<Results<Ok<BalanceResponse>, ProblemHttpResult>> GetBalanceAsync(
        Guid accountId, DateTimeOffset? at, LedgerService service, CancellationToken cancellationToken)
    {
        var result = await service.GetBalanceAsync(accountId, at, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(BalanceResponse.From(result.Value)) : result.Error!.ToProblem();
    }

    private static async Task<Results<Created<EntryResponse>, Ok<EntryResponse>, ProblemHttpResult>> RegisterMovementAsync(
        Guid accountId,
        RegisterMovementRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        LedgerService service,
        LedgerMetrics metrics,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request.Type is null || request.Amount is null)
        {
            metrics.WriteRejected("invalid_request");
            return LedgerErrors.Validation("invalid_request", "Os campos 'type' e 'amount' são obrigatórios.").ToProblem();
        }

        var result = await service.RegisterMovementAsync(
            accountId, request.Type.Value, request.Amount.Value, request.Description, idempotencyKey,
            ToOperationContext(httpContext), cancellationToken);

        return ToWriteResult(result, metrics, httpContext);
    }

    private static async Task<Results<Created<EntryResponse>, Ok<EntryResponse>, ProblemHttpResult>> ReverseEntryAsync(
        Guid accountId,
        Guid entryId,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        LedgerService service,
        LedgerMetrics metrics,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await service.ReverseEntryAsync(accountId, entryId, idempotencyKey, ToOperationContext(httpContext), cancellationToken);
        return ToWriteResult(result, metrics, httpContext);
    }

    private static async Task<Results<Ok<StatementResponse>, ProblemHttpResult>> GetStatementAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        LedgerService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetStatementAsync(accountId, from, to, cursor, limit, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var page = result.Value;
        return TypedResults.Ok(new StatementResponse(
            page.Items.Select(EntryResponse.From).ToList(),
            page.NextBeforeSequence is { } next ? StatementCursor.Encode(next) : null));
    }

    private static async Task<Results<Ok<EntryResponse>, ProblemHttpResult>> GetEntryAsync(
        Guid accountId, Guid entryId, LedgerService service, CancellationToken cancellationToken)
    {
        var result = await service.GetEntryAsync(accountId, entryId, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(EntryResponse.From(result.Value)) : result.Error!.ToProblem();
    }

    /// <summary>
    /// 201 na primeira execução; 200 + <c>Idempotent-Replayed: true</c> quando a chave já havia sido processada.
    /// O corpo é o mesmo nos dois casos, então o cliente trata ambos como sucesso.
    /// </summary>
    private static Results<Created<EntryResponse>, Ok<EntryResponse>, ProblemHttpResult> ToWriteResult(
        Result<MovementOutcome> result, LedgerMetrics metrics, HttpContext httpContext)
    {
        if (!result.IsSuccess)
        {
            metrics.WriteRejected(result.Error!.Code);
            return result.Error.ToProblem();
        }

        var entry = EntryResponse.From(result.Value.Entry);
        if (result.Value.Replayed)
        {
            metrics.IdempotentReplay();
            httpContext.Response.Headers[IdempotentReplayedHeader] = "true";
            return TypedResults.Ok(entry);
        }

        metrics.EntryRecorded(result.Value.Entry);
        return TypedResults.Created($"/v1/accounts/{entry.AccountId}/movements/{entry.Id}", entry);
    }

    private static OperationContext ToOperationContext(HttpContext httpContext) =>
        new(httpContext.User.GetClientId() ?? throw new InvalidOperationException("Política de escrita exige client id."),
            Activity.Current?.TraceId.ToHexString() ?? httpContext.TraceIdentifier);
}
