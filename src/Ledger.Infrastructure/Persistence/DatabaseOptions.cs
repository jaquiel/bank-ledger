namespace Ledger.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Aplica migrations ao iniciar. Em produção, prefira um passo dedicado no pipeline de deploy.</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>Tempo máximo de espera pelo lock de uma conta. Evita filas infinitas em contas muito disputadas.</summary>
    public int LockTimeoutMs { get; set; } = 3_000;

    /// <summary>Tempo máximo de qualquer comando. Requisições penduradas consomem conexões e derrubam o sistema em pico.</summary>
    public int StatementTimeoutMs { get; set; } = 5_000;

    /// <summary>Tentativas extras para falhas transitórias. Seguro porque toda escrita é idempotente.</summary>
    public int MaxTransientRetries { get; set; } = 2;
}
