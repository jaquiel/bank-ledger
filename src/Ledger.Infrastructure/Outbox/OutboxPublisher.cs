using Dapper;
using Ledger.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ledger.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; set; } = true;

    public int PollingIntervalMs { get; set; } = 1_000;

    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Publica os eventos gravados no outbox. Entrega "pelo menos uma vez": consumidores devem deduplicar pelo id da mensagem.
/// <c>FOR UPDATE SKIP LOCKED</c> permite várias instâncias da API publicarem em paralelo sem duplicar o trabalho.
/// Falha do broker não afeta o registro de movimentações: os eventos ficam acumulados e são entregues depois.
/// </summary>
public sealed partial class OutboxPublisher(
    NpgsqlDataSource dataSource,
    IIntegrationEventPublisher publisher,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.PollingIntervalMs), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    while (await PublishBatchAsync(settings.BatchSize, stoppingToken) == settings.BatchSize)
                    {
                        // Drena o backlog sem esperar o próximo tick.
                    }
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
                {
                    // Banco indisponível: tenta de novo no próximo ciclo. Nada se perde.
                    LogBatchFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal.
        }
    }

    private async Task<int> PublishBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var messages = (await connection.QueryAsync<OutboxRowView>(new CommandDefinition(
            """
            SELECT id AS Id, type AS Type, payload::text AS Payload
              FROM outbox_messages
             WHERE published_at IS NULL
             ORDER BY occurred_at
             LIMIT @BatchSize
               FOR UPDATE SKIP LOCKED
            """,
            new { BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        foreach (var message in messages)
        {
            try
            {
                await publisher.PublishAsync(message.Id, message.Type, message.Payload, cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE outbox_messages SET published_at = clock_timestamp(), attempts = attempts + 1 WHERE id = @Id",
                    new { message.Id },
                    transaction,
                    cancellationToken: cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not NpgsqlException)
            {
                // Falha do broker para uma mensagem não bloqueia as demais.
                LogPublishFailed(logger, message.Id, ex);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE outbox_messages SET attempts = attempts + 1, last_error = @Error WHERE id = @Id",
                    new { message.Id, Error = ex.Message },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao processar lote do outbox; nova tentativa no próximo ciclo.")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao publicar a mensagem {MessageId} do outbox.")]
    private static partial void LogPublishFailed(ILogger logger, Guid messageId, Exception exception);

    public sealed class OutboxRowView
    {
        public Guid Id { get; init; }

        public string Type { get; init; } = string.Empty;

        public string Payload { get; init; } = string.Empty;
    }
}

/// <summary>Publicador de referência: registra o evento no log. Substituível por um broker (Service Bus, Kafka) sem tocar no ledger.</summary>
public sealed partial class LoggingIntegrationEventPublisher(ILogger<LoggingIntegrationEventPublisher> logger) : IIntegrationEventPublisher
{
    public Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken)
    {
        LogPublished(logger, type, messageId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento {EventType} publicado (mensagem {MessageId}).")]
    private static partial void LogPublished(ILogger logger, string eventType, Guid messageId);
}
