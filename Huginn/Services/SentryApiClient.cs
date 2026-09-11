using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

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

    /// <summary>Every project in the organisation, for the settings picker.</summary>
    public async Task<List<string>> GetProjectSlugsAsync(
        string organizationSlug, CancellationToken ct = default)
    {
        using JsonDocument doc = await GetAsync(
            $"{_baseUrl}/organizations/{Uri.EscapeDataString(organizationSlug)}/projects/", ct);

        List<string> slugs = [];
        foreach (JsonElement project in doc.RootElement.EnumerateArray())
        {
            string? slug = project.TryGetProperty("slug", out JsonElement e) ? e.GetString() : null;
            if (!string.IsNullOrEmpty(slug)) slugs.Add(slug);
        }

        slugs.Sort(StringComparer.OrdinalIgnoreCase);
        return slugs;
    }

    /// <summary>
    /// Unresolved issues across the organisation, newest activity first.
    /// </summary>
    /// <param name="projectSlugs">
    /// The projects to report on. Empty returns nothing: watching everything by default would
    /// make the noisiest setting the one you get without choosing.
    /// </param>
    public async Task<List<SentryIssueItem>> GetIssuesAsync(
        string organizationSlug,
        IReadOnlyCollection<string> projectSlugs,
        string statsPeriod = "24h",
        int limit = 100,
        CancellationToken ct = default)
    {
        string url = $"{_baseUrl}/organizations/{Uri.EscapeDataString(organizationSlug)}/issues/"
                   + $"?query={Uri.EscapeDataString("is:unresolved")}"
                   + $"&statsPeriod={Uri.EscapeDataString(statsPeriod)}"
                   + $"&limit={limit}&sort=date";

        using JsonDocument doc = await GetAsync(url, ct);

        HashSet<string> wanted = new(projectSlugs, StringComparer.OrdinalIgnoreCase);
        List<SentryIssueItem> issues = [];
        if (wanted.Count == 0) return issues;
        foreach (JsonElement issue in doc.RootElement.EnumerateArray())
        {
            string project = issue.TryGetProperty("project", out JsonElement p)
                             && p.TryGetProperty("slug", out JsonElement ps)
                ? ps.GetString() ?? "" : "";

            // Filtering here rather than in the query keeps one request for any number of projects.
            if (!wanted.Contains(project)) continue;

            issues.Add(new SentryIssueItem
            {
                Id = Text(issue, "id"),
                ShortId = Text(issue, "shortId"),
                Title = Text(issue, "title"),
                Culprit = Text(issue, "culprit"),
                Level = Text(issue, "level"),
                ProjectSlug = project,
                // count comes back as a string, not a number.
                EventCount = int.TryParse(Text(issue, "count"), out int c) ? c : 0,
                UserCount = issue.TryGetProperty("userCount", out JsonElement u)
                            && u.TryGetInt32(out int uc) ? uc : 0,
                FirstSeen = Time(issue, "firstSeen"),
                LastSeen = Time(issue, "lastSeen"),
                Permalink = Text(issue, "permalink"),
                IsRegression = string.Equals(Text(issue, "substatus"), "regressed", StringComparison.OrdinalIgnoreCase),
            });
        }

        return issues;
    }

    /// <summary>
    /// First and last app version an issue has been seen in. These are only on the issue detail
    /// endpoint, so this is a second request per issue and the caller should cache it.
    /// </summary>
    /// <remarks>
    /// The path must be organisation scoped. The bare /issues/{id}/ form is legacy and answers 404
    /// on a region host.
    /// </remarks>
    public async Task<(string First, string Last)> GetIssueReleasesAsync(
        string organizationSlug, string issueId, CancellationToken ct = default)
    {
        using JsonDocument doc = await GetAsync(
            $"{_baseUrl}/organizations/{Uri.EscapeDataString(organizationSlug)}"
            + $"/issues/{Uri.EscapeDataString(issueId)}/", ct);

        return (Version(doc.RootElement, "firstRelease"), Version(doc.RootElement, "lastRelease"));
    }

    /// <summary>
    /// The version a human would recognise: no package prefix, no build ordinal.
    /// </summary>
    /// <remarks>
    /// Mobile releases arrive as "package@1.2.3+456". The package repeats the project and the
    /// ordinal after "+" is build metadata, so neither helps decide whether a fix has shipped.
    /// </remarks>
    private static string Version(JsonElement issue, string property)
    {
        if (!issue.TryGetProperty(property, out JsonElement release)
            || release.ValueKind != JsonValueKind.Object)
            return "";

        string shortVersion = Text(release, "shortVersion");
        string version = shortVersion.Length > 0 ? shortVersion : Text(release, "version");

        int at = version.LastIndexOf('@');
        if (at >= 0) version = version[(at + 1)..];

        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];

        return version;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

    private static DateTimeOffset Time(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement e)
        && DateTimeOffset.TryParse(e.GetString(), out DateTimeOffset value)
            ? value : DateTimeOffset.MinValue;

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        Log.Info($"GET {url}");
        HttpResponseMessage resp = await _http.GetAsync(url, ct);
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

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();
}
