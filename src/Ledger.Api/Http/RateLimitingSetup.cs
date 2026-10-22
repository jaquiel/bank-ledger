using System.Globalization;
using System.Threading.RateLimiting;
using Ledger.Api.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ledger.Api.Http;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Rajada máxima por cliente.</summary>
    public int TokenLimit { get; set; } = 200;

    /// <summary>Taxa sustentada por cliente (tokens repostos por período).</summary>
    public int TokensPerPeriod { get; set; } = 100;

    public int ReplenishmentPeriodMs { get; set; } = 1_000;
}

/// <summary>
/// Limite por cliente (token bucket). Protege o banco contra um cliente ruidoso e transforma sobrecarga
/// em 429 rápido, em vez de latência crescente para todos. Sem fila: em pico, rejeitar cedo é melhor que enfileirar.
/// </summary>
public static class RateLimitingSetup
{
    public const string PolicyName = "per-client";

    public static IServiceCollection AddLedgerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>().Bind(configuration.GetSection(RateLimitingOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(PolicyName, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var partitionKey = httpContext.User.GetClientId()
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";

                return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = settings.TokenLimit,
                    TokensPerPeriod = settings.TokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromMilliseconds(settings.ReplenishmentPeriodMs),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Limite de requisições excedido",
                        Detail = "Aguarde o tempo indicado em Retry-After antes de tentar novamente.",
                        Type = ProblemResults.TypeBaseUri + "rate_limited",
                        Extensions = { ["code"] = "rate_limited" },
                    },
                });
            };
        });

        return services;
    }
}
