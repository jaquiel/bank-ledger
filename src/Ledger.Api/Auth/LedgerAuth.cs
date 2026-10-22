using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Api.Auth;

public static class Scopes
{
    public const string Read = "ledger:read";
    public const string Write = "ledger:write";
}

public static class Policies
{
    public const string Read = "ledger.read";
    public const string Write = "ledger.write";
}

public sealed class LedgerAuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Emissor OIDC (ex.: Entra ID, Keycloak). Obrigatório fora de Development/Testing.</summary>
    public string? Authority { get; set; }

    public string Audience { get; set; } = "ledger-api";

    /// <summary>Chave simétrica SOMENTE para desenvolvimento local e testes. Nunca aceita em produção.</summary>
    public string? DevSigningKey { get; set; }

    public string DevIssuer { get; set; } = "ledger-dev";

    /// <summary>Habilita POST /dev/token (apenas em Development).</summary>
    public bool EnableDevTokenEndpoint { get; set; }
}

/// <summary>Falha na subida (não na primeira requisição) se a autenticação estiver mal configurada.</summary>
public sealed class LedgerAuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<LedgerAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, LedgerAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            return ValidateOptionsResult.Success;
        }

        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            return ValidateOptionsResult.Fail("Auth:Authority é obrigatório fora de Development/Testing. Chaves de desenvolvimento não são aceitas.");
        }

        return options.DevSigningKey is { Length: >= DevTokenIssuer.MinimumKeyLength }
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Auth:DevSigningKey precisa de ao menos {DevTokenIssuer.MinimumKeyLength} caracteres.");
    }
}

public static class LedgerAuthExtensions
{
    public static IServiceCollection AddLedgerAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LedgerAuthOptions>().Bind(configuration.GetSection(LedgerAuthOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LedgerAuthOptions>, LedgerAuthOptionsValidator>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<LedgerAuthOptions>>((jwt, auth) =>
            {
                var options = auth.Value;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidateAudience = true;
                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);

                if (!string.IsNullOrWhiteSpace(options.Authority))
                {
                    jwt.Authority = options.Authority;
                    return;
                }

                jwt.TokenValidationParameters.ValidIssuer = options.DevIssuer;
                jwt.TokenValidationParameters.IssuerSigningKey = DevTokenIssuer.CreateKey(options.DevSigningKey ?? string.Empty);
            });

        // Menor privilégio: leitura e escrita são escopos distintos, e escrita exige identificar o cliente
        // (a identidade compõe o escopo da Idempotency-Key e a trilha de auditoria).
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Read, p => p.RequireAuthenticatedUser().RequireAssertion(ctx => ctx.User.HasScope(Scopes.Read)))
            .AddPolicy(Policies.Write, p => p.RequireAuthenticatedUser().RequireAssertion(
                ctx => ctx.User.HasScope(Scopes.Write) && ctx.User.GetClientId() is not null));

        return services;
    }

    public static bool HasScope(this ClaimsPrincipal user, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
    }

    public static string? GetClientId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindFirst("client_id")?.Value ?? user.FindFirst("azp")?.Value ?? user.FindFirst("sub")?.Value;
    }
}

/// <summary>Emissor de tokens de desenvolvimento/teste. Em produção, tokens vêm do provedor OIDC.</summary>
public static class DevTokenIssuer
{
    public const int MinimumKeyLength = 32;

    public static SymmetricSecurityKey CreateKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    public static string Issue(LedgerAuthOptions options, string clientId, IEnumerable<string> scopes, TimeSpan lifetime, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.DevIssuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = clientId,
                ["client_id"] = clientId,
                ["scope"] = string.Join(' ', scopes),
            },
            SigningCredentials = new SigningCredentials(CreateKey(options.DevSigningKey ?? string.Empty), SecurityAlgorithms.HmacSha256),
        });
    }
}
