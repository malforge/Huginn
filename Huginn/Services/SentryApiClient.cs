using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Huginn.Services;

/// <summary>
/// Talks to the Sentry REST API on behalf of one organisation.
/// </summary>
/// <remarks>
/// The base URL is region-specific: an EU-resident organisation answers on de.sentry.io and returns
/// nothing useful from sentry.io, so it comes from settings rather than being hardcoded.
/// </remarks>
public sealed class SentryApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _baseUrl;

    public event Action<string>? AuthFailed;

    public SentryApiClient(string apiBaseUrl, string token)
    {
        _baseUrl = apiBaseUrl.TrimEnd('/');
        Log.Info($"SentryApiClient created: baseUrl={_baseUrl}");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Resolves the organisation's display name, which doubles as an authentication check.
    /// Returns an empty string when the token is rejected, having raised <see cref="AuthFailed"/>.
    /// </summary>
    public async Task<string> GetOrganizationNameAsync(string organizationSlug, CancellationToken ct = default)
    {
        string url = $"{_baseUrl}/organizations/{Uri.EscapeDataString(organizationSlug)}/";
        Log.Info($"GET {url}");
        HttpResponseMessage resp = await _http.GetAsync(url, ct);
        Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            Log.Error($"  Body: {body}");

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                AuthFailed?.Invoke(resp.StatusCode.ToString());
                return "";
            }

            throw new HttpRequestException(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 200)}");
        }

        using JsonDocument doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        return doc.RootElement.TryGetProperty("name", out JsonElement name)
            ? name.GetString() ?? organizationSlug
            : organizationSlug;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();
}
