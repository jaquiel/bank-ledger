using System.Text;
using Dapper;
using Ledger.Application;
using Ledger.Domain;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Leituras. Todas são consultas indexadas de custo O(log n), independentes do volume de lançamentos da conta.
/// Em produção, este reader é o candidato natural a apontar para uma réplica de leitura (ver ADR 0006).
/// </summary>
public sealed class PostgresLedgerReader(NpgsqlDataSource dataSource) : ILedgerReader
{
    public async Task<Account?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            $"SELECT {AccountRow.Columns} FROM accounts WHERE id = @AccountId",
            new { AccountId = accountId },
            cancellationToken: cancellationToken));
        return row?.ToDomain();
    }

    public async Task<BalanceSnapshot?> GetBalanceAsync(Guid accountId, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        if (at is null)
        {
            var current = await connection.QuerySingleOrDefaultAsync<BalanceRow>(new CommandDefinition(
                """
                SELECT currency AS Currency, balance AS Balance, version AS LastSequence, clock_timestamp() AS AsOf
                  FROM accounts
                 WHERE id = @AccountId
                """,
                new { AccountId = accountId },
                cancellationToken: cancellationToken));
            return current is null ? null : ToSnapshot(accountId, current);
        }

        // Posição em T = balance_after do último lançamento com occurred_at <= T.
        // Antes do primeiro lançamento, o saldo é zero.
        var historical = await connection.QuerySingleOrDefaultAsync<BalanceRow>(new CommandDefinition(
            """
            SELECT a.currency                        AS Currency,
                   COALESCE(last.balance_after, 0)   AS Balance,
                   COALESCE(last.sequence, 0)        AS LastSequence,
                   @At                               AS AsOf
              FROM accounts a
              LEFT JOIN LATERAL (
                    SELECT e.balance_after, e.sequence
                      FROM ledger_entries e
                     WHERE e.account_id = a.id
                       AND e.occurred_at <= @At
                     ORDER BY e.occurred_at DESC, e.sequence DESC
                     LIMIT 1
                   ) last ON true
             WHERE a.id = @AccountId
            """,
            new { AccountId = accountId, At = at.Value.UtcDateTime },
            cancellationToken: cancellationToken));
        return historical is null ? null : ToSnapshot(accountId, historical);
    }

    public async Task<StatementPage?> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM accounts WHERE id = @AccountId)",
            new { query.AccountId },
            cancellationToken: cancellationToken));
        if (!exists)
        {
            return null;
        }

        // SQL montado só com filtros presentes: planos simples e previsíveis (sem "@x IS NULL OR ...").
        var sql = new StringBuilder($"SELECT {EntryRow.Columns} FROM ledger_entries WHERE account_id = @AccountId");
        var parameters = new DynamicParameters();
        parameters.Add("AccountId", query.AccountId);
        parameters.Add("Take", query.Limit + 1);

        if (query.BeforeSequence is { } before)
        {
            sql.Append(" AND sequence < @Before");
            parameters.Add("Before", before);
        }

        if (query.From is { } from)
        {
            sql.Append(" AND occurred_at >= @From");
            parameters.Add("From", from.UtcDateTime);
        }

        if (query.To is { } to)
        {
            sql.Append(" AND occurred_at <= @To");
            parameters.Add("To", to.UtcDateTime);
        }

        sql.Append(" ORDER BY sequence DESC LIMIT @Take");

        var rows = (await connection.QueryAsync<EntryRow>(new CommandDefinition(
            sql.ToString(), parameters, cancellationToken: cancellationToken))).ToList();

        var hasMore = rows.Count > query.Limit;
        var items = rows.Take(query.Limit).Select(r => r.ToDomain()).ToList();
        return new StatementPage(items, hasMore ? items[^1].Sequence : null);
    }

    public async Task<LedgerEntry?> GetEntryAsync(Guid accountId, Guid entryId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition(
            $"SELECT {EntryRow.Columns} FROM ledger_entries WHERE id = @EntryId AND account_id = @AccountId",
            new { EntryId = entryId, AccountId = accountId },
            cancellationToken: cancellationToken));
        return row?.ToDomain();
    }

    private static BalanceSnapshot ToSnapshot(Guid accountId, BalanceRow row) =>
        new(accountId, row.Currency.Trim(), row.Balance, DbTime.ToOffset(row.AsOf), row.LastSequence);
}
