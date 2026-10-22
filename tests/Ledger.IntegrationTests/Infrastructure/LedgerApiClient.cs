using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Cliente fino sobre a API HTTP, para os testes lerem como cenários de negócio.</summary>
public sealed class LedgerApiClient(HttpClient http) : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HttpClient Http => http;

    public async Task<AccountResponse> CreateAccountAsync(decimal overdraftLimit = 0)
    {
        using var response = await http.PostAsJsonAsync("/v1/accounts", new { overdraftLimit }, JsonOptions, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AccountResponse>(JsonOptions, Ct))!;
    }

    public Task<HttpResponseMessage> PostMovementAsync(Guid accountId, string type, decimal amount, string? idempotencyKey = null, string? description = null) =>
        SendAsync(HttpMethod.Post, $"/v1/accounts/{accountId}/movements", new { type, amount, description }, idempotencyKey ?? Guid.NewGuid().ToString());

    public Task<HttpResponseMessage> ReverseAsync(Guid accountId, Guid entryId, string? idempotencyKey = null) =>
        SendAsync(HttpMethod.Post, $"/v1/accounts/{accountId}/movements/{entryId}/reversal", null, idempotencyKey ?? Guid.NewGuid().ToString());

    public async Task<EntryResponse> CreditAsync(Guid accountId, decimal amount) => await RegisterAsync(accountId, "credit", amount);

    public async Task<EntryResponse> DebitAsync(Guid accountId, decimal amount) => await RegisterAsync(accountId, "debit", amount);

    public async Task<BalanceResponse> GetBalanceAsync(Guid accountId, DateTimeOffset? at = null)
    {
        using var response = await GetBalanceResponseAsync(accountId, at);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BalanceResponse>(JsonOptions, Ct))!;
    }

    public Task<HttpResponseMessage> GetBalanceResponseAsync(Guid accountId, DateTimeOffset? at = null)
    {
        var query = at is null ? string.Empty : "?at=" + Uri.EscapeDataString(at.Value.ToString("O", CultureInfo.InvariantCulture));
        return http.GetAsync($"/v1/accounts/{accountId}/balance{query}", Ct);
    }

    public async Task<StatementResponse> GetStatementAsync(Guid accountId, string query = "")
    {
        using var response = await http.GetAsync($"/v1/accounts/{accountId}/movements{query}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<StatementResponse>(JsonOptions, Ct))!;
    }

    public async Task<IReadOnlyList<EntryResponse>> GetAllEntriesAsync(Guid accountId)
    {
        var all = new List<EntryResponse>();
        string? cursor = null;
        do
        {
            var page = await GetStatementAsync(accountId, cursor is null ? "?limit=200" : $"?limit=200&cursor={cursor}");
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return all;
    }

    public static async Task<EntryResponse> ReadEntryAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<EntryResponse>(JsonOptions, Ct))!;

    public static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions, Ct);
        return problem?.Extensions.TryGetValue("code", out var code) == true ? code?.ToString() : null;
    }

    public void Dispose() => http.Dispose();

    private async Task<EntryResponse> RegisterAsync(Guid accountId, string type, decimal amount)
    {
        using var response = await PostMovementAsync(accountId, type, amount);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await ReadEntryAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, object? body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await http.SendAsync(request, Ct);
    }
}
