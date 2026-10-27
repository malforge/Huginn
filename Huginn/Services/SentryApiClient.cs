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

    /// <summary>Issues per page, the most Sentry hands out at once.</summary>
    private const int PageSize = 100;

    /// <summary>
    /// Ceiling on pages read per poll. Each is its own request, and an organization with more than
    /// this many unresolved issues in the window is told so rather than shown a silently cut list.
    /// </summary>
    private const int MaxPages = 10;

    /// <summary>
    /// Unresolved issues seen within <see cref="SentryIssueItem.RecentWindow"/>, every page of
    /// them up to <see cref="MaxPages"/>.
    /// </summary>
    /// <param name="projectSlugs">
    /// The projects to report on. Empty returns nothing: watching everything by default would
    /// make the noisiest setting the one you get without choosing.
    /// </param>
    /// <returns>
    /// The issues, and whether that is all of them. Pages are asked for by reach in the window,
    /// so a list cut at the ceiling loses the issues that reached fewest people lately.
    /// </returns>
    public async Task<(List<SentryIssueItem> Issues, bool IsComplete)> GetIssuesAsync(
        string organizationSlug,
        IReadOnlyCollection<string> projectSlugs,
        CancellationToken ct = default)
    {
        List<SentryIssueItem> issues = [];
        if (projectSlugs.Count == 0) return (issues, true);

        // statsPeriod is the date filter. It scopes which issues come back and the top-level
        // counts, while lifetime carries the totals Sentry itself shows. groupStatsPeriod is the
        // sparkline alone.
        string url = $"{_baseUrl}/organizations/{Uri.EscapeDataString(organizationSlug)}/issues/"
                   + $"?query={Uri.EscapeDataString("is:unresolved")}"
                   + $"&statsPeriod={SentryIssueItem.RecentWindow}"
                   + $"&groupStatsPeriod={SeriesPeriod}"
                   + $"&limit={PageSize}&sort=user";

        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            string pageUrl = cursor is null ? url : $"{url}&cursor={Uri.EscapeDataString(cursor)}";
            (JsonDocument doc, string? link) = await GetPageAsync(pageUrl, ct);
            using (doc) issues.AddRange(ParseIssues(doc.RootElement, projectSlugs));

            cursor = NextCursor(link);
            if (cursor is null) return (issues, true);
        }

        return (issues, false);
    }

    /// <summary>The sparkline window, read back from the response under the same key.</summary>
    private const string SeriesPeriod = "24h";

    /// <summary>One page of the issue list, keeping the watched projects.</summary>
    /// <remarks>
    /// Filtering here rather than in the query keeps one listing for any number of projects. Reach
    /// and first seen come from lifetime: the top level is cut to the queried window, so read
    /// alone it calls a crash from April new and counts only its last fortnight.
    /// </remarks>
    public static List<SentryIssueItem> ParseIssues(JsonElement page, IReadOnlyCollection<string> projectSlugs)
    {
        HashSet<string> wanted = new(projectSlugs, StringComparer.OrdinalIgnoreCase);
        List<SentryIssueItem> issues = [];
        if (wanted.Count == 0) return issues;

        foreach (JsonElement issue in page.EnumerateArray())
        {
            string project = issue.TryGetProperty("project", out JsonElement p)
                             && p.TryGetProperty("slug", out JsonElement ps)
                ? ps.GetString() ?? "" : "";

            if (!wanted.Contains(project)) continue;

            JsonElement lifetime = issue.TryGetProperty("lifetime", out JsonElement l)
                                   && l.ValueKind == JsonValueKind.Object
                ? l : issue;

            issues.Add(new SentryIssueItem
            {
                Id = Text(issue, "id"),
                ShortId = Text(issue, "shortId"),
                Title = Text(issue, "title"),
                Culprit = Text(issue, "culprit"),
                Level = Text(issue, "level"),
                ProjectSlug = project,
                EventCount = Events(lifetime),
                UserCount = Users(lifetime),
                RecentEventCount = Events(issue),
                RecentUserCount = Users(issue),
                FirstSeen = Time(lifetime, "firstSeen"),
                LastSeen = Time(lifetime, "lastSeen"),
                Permalink = Text(issue, "permalink"),
                IsRegression = string.Equals(Text(issue, "substatus"), "regressed", StringComparison.OrdinalIgnoreCase),
                EventSeries = Series(issue, SeriesPeriod, out int bucketMinutes),
                SeriesBucketMinutes = bucketMinutes,
                AssigneeId = Assignee(issue, "id"),
                AssigneeName = Assignee(issue, "name"),
                AssigneeIsTeam = Assignee(issue, "type") == "team",
            });
        }

        return issues;
    }

    /// <summary>One field of assignedTo, which is null when nobody has the issue.</summary>
    private static string Assignee(JsonElement issue, string property) =>
        issue.TryGetProperty("assignedTo", out JsonElement to) && to.ValueKind == JsonValueKind.Object
            ? Text(to, property)
            : "";

    /// <summary>count comes back as a string, not a number.</summary>
    private static int Events(JsonElement counts) =>
        int.TryParse(Text(counts, "count"), out int c) ? c : 0;

    private static int Users(JsonElement counts) =>
        counts.TryGetProperty("userCount", out JsonElement u) && u.TryGetInt32(out int uc) ? uc : 0;

    /// <summary>
    /// The cursor of the next page, from a Link header. Sentry always sends a next link, and says
    /// whether anything is behind it in its results attribute.
    /// </summary>
    public static string? NextCursor(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;

        foreach (string part in link.Split(','))
        {
            if (!part.Contains("rel=\"next\"", StringComparison.Ordinal)) continue;
            if (!part.Contains("results=\"true\"", StringComparison.Ordinal)) return null;

            const string marker = "cursor=\"";
            int start = part.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            int end = part.IndexOf('"', start);
            return end < 0 ? null : part[start..end];
        }

        return null;
    }

    /// <summary>
    /// Event counts per bucket, which the list response already carries under the stats period
    /// that was asked for. No extra request, so every issue gets one.
    /// </summary>
    /// <param name="bucketMinutes">
    /// Taken from the gap between the first two timestamps rather than assumed, so it stays right
    /// if the stats period changes.
    /// </param>
    private static IReadOnlyList<double> Series(JsonElement issue, string statsPeriod, out int bucketMinutes)
    {
        bucketMinutes = 0;

        if (!issue.TryGetProperty("stats", out JsonElement stats)
            || !stats.TryGetProperty(statsPeriod, out JsonElement points)
            || points.ValueKind != JsonValueKind.Array)
            return [];

        List<double> counts = [];
        long firstAt = 0, secondAt = 0;

        foreach (JsonElement point in points.EnumerateArray())
        {
            // Each point is [unixSeconds, count].
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;

            long at = point[0].TryGetInt64(out long seconds) ? seconds : 0;
            if (counts.Count == 0) firstAt = at;
            else if (counts.Count == 1) secondAt = at;

            counts.Add(point[1].TryGetDouble(out double count) ? count : 0);
        }

        if (secondAt > firstAt) bucketMinutes = (int)((secondAt - firstAt) / 60);

        return counts;
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

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct) =>
        (await GetPageAsync(url, ct)).Doc;

    /// <summary>A response with the Link header that says where the next page is.</summary>
    private async Task<(JsonDocument Doc, string? Link)> GetPageAsync(string url, CancellationToken ct)
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

        string? link = resp.Headers.TryGetValues("Link", out IEnumerable<string>? values)
            ? string.Join(",", values) : null;

        return (await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct), link);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();
}
