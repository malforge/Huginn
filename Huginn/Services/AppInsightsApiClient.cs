using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Huginn.Models;

namespace Huginn.Services;

/// <summary>
/// Reads Azure Resource Manager and the Application Insights query API as the signed-in user.
/// </summary>
/// <remarks>
/// The two endpoints need tokens for different scopes, so the credential is held rather than a
/// single fixed token. Everything returned is therefore scoped to what that person can actually see.
/// </remarks>
public sealed class AppInsightsApiClient : IDisposable
{
    private const string Arm = "https://management.azure.com";
    private const string Query = "https://api.applicationinsights.io/v1";

    private readonly HttpClient _http = new();
    private readonly TokenCredential _credential;

    public event Action<string>? AuthFailed;

    public AppInsightsApiClient(TokenCredential credential)
    {
        _credential = credential;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Every Application Insights resource the user can read, across all subscriptions.</summary>
    public async Task<List<AppInsightsComponent>> GetComponentsAsync(CancellationToken ct = default)
    {
        List<AppInsightsComponent> components = [];

        using JsonDocument subs = await GetJsonAsync(
            $"{Arm}/subscriptions?api-version=2022-12-01", ArmToken, ct);

        foreach (JsonElement sub in subs.RootElement.GetProperty("value").EnumerateArray())
        {
            string id = sub.GetProperty("subscriptionId").GetString() ?? "";
            string name = sub.TryGetProperty("displayName", out JsonElement d) ? d.GetString() ?? id : id;
            if (id.Length == 0) continue;

            using JsonDocument found = await GetJsonAsync(
                $"{Arm}/subscriptions/{id}/providers/Microsoft.Insights/components?api-version=2020-02-02",
                ArmToken, ct);

            foreach (JsonElement c in found.RootElement.GetProperty("value").EnumerateArray())
            {
                components.Add(new AppInsightsComponent
                {
                    Name = c.GetProperty("name").GetString() ?? "",
                    AppId = c.GetProperty("properties").TryGetProperty("AppId", out JsonElement a)
                        ? a.GetString() ?? "" : "",
                    SubscriptionName = name,
                    ResourceGroup = ResourceGroupOf(c),
                });
            }
        }

        components.RemoveAll(c => c.AppId.Length == 0);
        components.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        return components;
    }

    /// <summary>Runs a KQL query against one component and returns the first table.</summary>
    public async Task<QueryTable> QueryAsync(string appId, string kql, CancellationToken ct = default)
    {
        string url = $"{Query}/apps/{Uri.EscapeDataString(appId)}/query?query={Uri.EscapeDataString(kql)}";
        using JsonDocument doc = await GetJsonAsync(url, QueryToken, ct);

        JsonElement table = doc.RootElement.GetProperty("tables")[0];

        List<string> columns = [];
        foreach (JsonElement c in table.GetProperty("columns").EnumerateArray())
            columns.Add(c.GetProperty("name").GetString() ?? "");

        List<List<JsonElement>> rows = [];
        foreach (JsonElement r in table.GetProperty("rows").EnumerateArray())
            rows.Add([.. r.EnumerateArray()]);

        return new QueryTable(columns, rows);
    }

    private async Task<string> ArmToken(CancellationToken ct) => await TokenFor(AzureSignIn.ArmScope, ct);

    private async Task<string> QueryToken(CancellationToken ct) => await TokenFor(AzureSignIn.QueryScope, ct);

    private async Task<string> TokenFor(string[] scopes, CancellationToken ct)
    {
        // Off the UI thread for the same reason the interactive sign-in is: the credential can do
        // meaningful synchronous work, including a silent refresh, before it yields.
        AccessToken token = await Task.Run(
            () => _credential.GetTokenAsync(new TokenRequestContext(scopes), ct).AsTask(), ct);
        return token.Token;
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url, Func<CancellationToken, Task<string>> token, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));

        Log.Info($"GET {Redact(url)}");
        HttpResponseMessage resp = await _http.SendAsync(request, ct);
        Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            Log.Error($"  Body: {Truncate(body, 400)}");

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                AuthFailed?.Invoke(resp.StatusCode.ToString());

            throw new HttpRequestException(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 200)}");
        }

        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>The resource group is only available inside the resource id.</summary>
    private static string ResourceGroupOf(JsonElement component)
    {
        string id = component.TryGetProperty("id", out JsonElement e) ? e.GetString() ?? "" : "";
        const string marker = "/resourceGroups/";
        int start = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += marker.Length;
        int end = id.IndexOf('/', start);
        return end < 0 ? id[start..] : id[start..end];
    }

    /// <summary>Keeps the KQL out of the log, since a query can carry identifiers.</summary>
    private static string Redact(string url)
    {
        int q = url.IndexOf("?query=", StringComparison.Ordinal);
        return q < 0 ? url : url[..q] + "?query=…";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();
}
