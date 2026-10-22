using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Auth;
using Ledger.Api.Endpoints;
using Ledger.Api.Http;
using Ledger.Api.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Ledger.Api;

public static class ApiSetup
{
    public static IServiceCollection AddLedgerApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(json =>
        {
            // Enums como texto ("credit"/"debit"); números não são aceitos para evitar ambiguidade.
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            json.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
        });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToHexString() ?? context.HttpContext.TraceIdentifier));
        services.AddExceptionHandler<InfrastructureExceptionHandler>();

        // Nenhuma requisição segura recursos indefinidamente.
        services.AddRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(10),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        });

        services.AddLedgerAuth(configuration);
        services.AddLedgerRateLimiting(configuration);
        services.AddLedgerObservability(configuration);
        services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
        return services;
    }

    public static WebApplication UseLedgerApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseRequestTimeouts();

        // Liveness não depende do banco (reiniciar a API não conserta o banco); readiness depende.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        // Documentação interativa e emissão de token local só em Development.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();

            if (app.Services.GetRequiredService<IOptions<LedgerAuthOptions>>().Value.EnableDevTokenEndpoint)
            {
                app.MapDevTokenEndpoint();
            }
        }

        app.MapLedgerEndpoints();
        return app;
    }
}

internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Em Development, obtenha um token em POST /dev/token.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, document)] = [] });
        return Task.CompletedTask;
    }
}
