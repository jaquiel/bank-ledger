using System.Diagnostics.Metrics;
using Ledger.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ledger.Api.Observability;

/// <summary>Métricas de negócio, além das técnicas (latência HTTP, pool de conexões) já instrumentadas.</summary>
public sealed class LedgerMetrics
{
    public const string MeterName = "Ledger.Api";

    private readonly Counter<long> _entriesRecorded;
    private readonly Counter<long> _writesRejected;
    private readonly Counter<long> _idempotentReplays;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _entriesRecorded = meter.CreateCounter<long>("ledger.entries.recorded", description: "Lançamentos registrados.");
        _writesRejected = meter.CreateCounter<long>("ledger.writes.rejected", description: "Escritas recusadas por regra de negócio ou validação.");
        _idempotentReplays = meter.CreateCounter<long>("ledger.idempotent.replays", description: "Requisições repetidas atendidas por replay.");
    }

    public void EntryRecorded(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entriesRecorded.Add(1,
            new KeyValuePair<string, object?>("type", entry.Type.ToCode()),
            new KeyValuePair<string, object?>("reversal", entry.ReversalOf is not null));
    }

    public void WriteRejected(string code) => _writesRejected.Add(1, new KeyValuePair<string, object?>("code", code));

    public void IdempotentReplay() => _idempotentReplays.Add(1);
}

/// <summary>Readiness: só recebe tráfego quem consegue falar com o banco.</summary>
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Banco de dados indisponível.", ex);
        }
    }
}

public static class ObservabilityExtensions
{
    public static IServiceCollection AddLedgerObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<LedgerMetrics>();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("ledger-api"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter(LedgerMetrics.MeterName)
                .AddMeter("Npgsql"))
            .WithLogging();

        // Exporta via OTLP quando configurado: Aspire Dashboard localmente; em produção, collector para
        // Prometheus/Grafana ou Azure Monitor. Trocar o destino é configuração, não código.
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return services;
    }
}
