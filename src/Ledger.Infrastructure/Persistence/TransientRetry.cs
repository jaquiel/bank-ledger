using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Retry curto e limitado para falhas transitórias do banco (queda de conexão, deadlock, failover).
/// É seguro reexecutar a transação inteira porque toda escrita carrega uma Idempotency-Key:
/// se a primeira tentativa chegou a commitar e só a resposta se perdeu, a segunda vira replay.
/// Falhas de contenção (lock_timeout) NÃO são repetidas aqui: insistir só aumentaria a fila.
/// </summary>
public sealed partial class TransientRetry(IOptions<DatabaseOptions> options, TimeProvider timeProvider, ILogger<TransientRetry> logger)
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var maxRetries = Math.Max(0, options.Value.MaxTransientRetries);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds((50 * Math.Pow(4, attempt)) + Random.Shared.Next(0, 50));
                LogRetry(logger, attempt + 1, delay.TotalMilliseconds, ex);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
    }

    public static bool IsTransient(Exception exception) => exception switch
    {
        // Contenção e timeout de comando são sinais de sobrecarga: repetir pioraria.
        PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled } => false,
        PostgresException pg => pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
            || pg.IsTransient,
        NpgsqlException npgsql => npgsql.IsTransient,
        TimeoutException => true,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha transitória no banco; tentativa {Attempt} em {DelayMs} ms.")]
    private static partial void LogRetry(ILogger logger, int attempt, double delayMs, Exception exception);
}
