using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Executor mínimo de migrations em SQL versionado (embutido no assembly).
/// SQL explícito foi escolhido de propósito: constraints, índices parciais e triggers são parte do design
/// e precisam ser revisáveis como código. Um advisory lock impede que duas instâncias migrem ao mesmo tempo.
/// </summary>
public sealed partial class DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger)
{
    private const long MigrationLockId = 7_300_202_610_210_001;
    private const string ResourcePrefix = "Ledger.Infrastructure.Persistence.Migrations.";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@id)", new { id = MigrationLockId }, cancellationToken: cancellationToken));

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    text        PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                )
                """,
                cancellationToken: cancellationToken));

            var applied = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT version FROM schema_migrations", cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);

            foreach (var (version, sql) in LoadMigrations())
            {
                if (applied.Contains(version))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations (version) VALUES (@version)",
                    new { version },
                    transaction,
                    cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
                LogMigrationApplied(logger, version);
            }
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@id)", new { id = MigrationLockId }, cancellationToken: CancellationToken.None));
        }
    }

    private static IEnumerable<(string Version, string Sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Migration {name} não encontrada.");
            using var reader = new StreamReader(stream);
            yield return (name[ResourcePrefix.Length..^".sql".Length], reader.ReadToEnd());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migration {Version} aplicada.")]
    private static partial void LogMigrationApplied(ILogger logger, string version);
}
