using System.Diagnostics;
using Ledger.Application;
using Ledger.Infrastructure.Outbox;
using Ledger.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledger.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Ledger";

    public static IServiceCollection AddLedgerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName));
        services.TryAddTimeProvider();

        // Resolvido de forma tardia (no primeiro uso), para respeitar configurações aplicadas por hosts de teste.
        services.AddSingleton(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} não configurada.");
            }

            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // Limites aplicados pelo próprio Postgres a cada sessão: nenhuma requisição espera para sempre.
                Options = $"-c lock_timeout={db.LockTimeoutMs} -c statement_timeout={db.StatementTimeoutMs}",
                CommandTimeout = (int)Math.Ceiling(db.StatementTimeoutMs / 1000d) + 1,
            };

            // Só gera span para comandos dentro de um trace existente (requisição HTTP). O polling do
            // outbox roda sem trace pai e, a cada segundo, viraria um trace solto que soterra os reais.
            return new NpgsqlDataSourceBuilder(builder.ConnectionString)
                .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
                .ConfigureTracing(tracing => tracing
                    .ConfigureCommandFilter(_ => Activity.Current is not null)
                    .ConfigureBatchFilter(_ => Activity.Current is not null))
                .Build();
        });

        services.AddSingleton<TransientRetry>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<ILedgerWriter, PostgresLedgerWriter>();
        services.AddSingleton<ILedgerReader, PostgresLedgerReader>();
        services.AddSingleton<IIntegrationEventPublisher, LoggingIntegrationEventPublisher>();
        services.AddScoped<LedgerService>();

        // Ordem importa: migrations antes do publicador do outbox.
        services.AddHostedService<DatabaseMigrationHostedService>();
        services.AddHostedService<OutboxPublisher>();
        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}

/// <summary>Aplica as migrations na subida, com novas tentativas enquanto o banco ainda está iniciando.</summary>
public sealed partial class DatabaseMigrationHostedService(
    DatabaseMigrator migrator,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    private const int MaxAttempts = 10;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyMigrationsOnStartup)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await migrator.MigrateAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException ex) when (attempt < MaxAttempts && ex.IsTransient)
            {
                LogWaitingForDatabase(logger, attempt, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Banco indisponível ao aplicar migrations (tentativa {Attempt}); aguardando.")]
    private static partial void LogWaitingForDatabase(ILogger logger, int attempt, Exception exception);
}
