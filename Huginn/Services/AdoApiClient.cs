using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services;

public sealed class AdoApiClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _baseUrl;
    private readonly string _org;
    private readonly string _project;
    private string _projectId = "";

    public event Action<string>? AuthFailed;

    public AdoApiClient(string organization, string project, string pat)
    {
        _org = organization;
        _project = project;
        _baseUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}";
        Log.Info($"AdoApiClient created: org={organization}, project={project}, baseUrl={_baseUrl}");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetMyProfileIdAsync(CancellationToken ct = default)
    {
        // connectiondata is preview-only at all API versions — this is permanent, not actually "beta"
        var url = $"https://dev.azure.com/{Uri.EscapeDataString(_org)}/_apis/connectiondata?api-version=7.0-preview";
        Log.Info($"GET {url}");
        var resp = await _http.GetAsync(url, ct);
        Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            Log.Error($"  Body: {body}");

            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                AuthFailed?.Invoke(resp.StatusCode.ToString());
                return "";
            }

            throw new HttpRequestException(
                $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 200)}");
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var id = doc.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString() ?? "";
        Log.Info($"  Identity ID: {id}");
        return id;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public async Task<List<PullRequestItem>> GetPullRequestsByReviewerAsync(string reviewerId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/_apis/git/pullrequests?searchCriteria.reviewerId={Uri.EscapeDataString(reviewerId)}&searchCriteria.status=active&api-version=7.0";
        return await FetchPullRequests(url, ct);
    }

    public async Task<List<PullRequestItem>> GetPullRequestsByCreatorAsync(string creatorId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/_apis/git/pullrequests?searchCriteria.creatorId={Uri.EscapeDataString(creatorId)}&searchCriteria.status=active&api-version=7.0";
        return await FetchPullRequests(url, ct);
    }

    private async Task<List<PullRequestItem>> FetchPullRequests(string url, CancellationToken ct)
    {
        var result = new List<PullRequestItem>();
        try
        {
            Log.Info($"GET {url}");
            var resp = await _http.GetAsync(url, ct);
            Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.Error($"  Body: {body}");

                if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden)
                {
                    AuthFailed?.Invoke(resp.StatusCode.ToString());
                    return result;
                }

                return result;
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var count = doc.RootElement.GetProperty("count").GetInt32();
            Log.Info($"  PR count: {count}");

            foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var reviewers = new List<ReviewerInfo>();
                if (pr.TryGetProperty("reviewers", out var revArr))
                {
                    foreach (var r in revArr.EnumerateArray())
                    {
                        reviewers.Add(new ReviewerInfo
                        {
                            Id = r.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "",
                            DisplayName = r.GetProperty("displayName").GetString() ?? "",
                            UniqueName = r.GetProperty("uniqueName").GetString() ?? "",
                            Vote = r.GetProperty("vote").GetInt32(),
                        });
                    }
                }

                var createdBy = pr.GetProperty("createdBy");
                var repo = pr.GetProperty("repository");
                var autoCompleteSet = pr.TryGetProperty("autoCompleteSetBy", out var ac)
                                      && ac.ValueKind == JsonValueKind.Object;
                var isDraft = pr.TryGetProperty("isDraft", out var draft) && draft.GetBoolean();

                result.Add(new PullRequestItem
                {
                    PullRequestId = pr.GetProperty("pullRequestId").GetInt32(),
                    Title = pr.GetProperty("title").GetString() ?? "",
                    Status = pr.GetProperty("status").GetString() ?? "",
                    CreatedByName = createdBy.GetProperty("displayName").GetString() ?? "",
                    RepositoryName = repo.GetProperty("name").GetString() ?? "",
                    RepositoryId = repo.GetProperty("id").GetString() ?? "",
                    CreationDate = pr.GetProperty("creationDate").GetDateTime(),
                    Reviewers = reviewers,
                    IsAutoCompleteSet = autoCompleteSet,
                    IsDraft = isDraft,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"FetchPullRequests exception: {ex}");
        }

        return result;
    }

    public async Task EnrichWithPipelineStatusAsync(List<PullRequestItem> prs, CancellationToken ct = default)
    {
        // We need the project ID (GUID) for policy evaluations — fetch once
        if (string.IsNullOrEmpty(_projectId))
        {
            var projUrl = $"https://dev.azure.com/{Uri.EscapeDataString(_org)}/_apis/projects/{Uri.EscapeDataString(_project)}?api-version=7.0";
            Log.Info($"GET {projUrl}");
            var projResp = await _http.GetAsync(projUrl, ct);
            Log.Info($"  → {(int)projResp.StatusCode} {projResp.ReasonPhrase}");
            if (!projResp.IsSuccessStatusCode) return;
            using var projDoc = await JsonDocument.ParseAsync(await projResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            _projectId = projDoc.RootElement.GetProperty("id").GetString() ?? "";
            Log.Info($"  Project ID: {_projectId}");
        }

        var tasks = prs.Select(pr => FetchPolicyEvaluationsAsync(pr, ct));
        await Task.WhenAll(tasks);
    }

    private async Task FetchPolicyEvaluationsAsync(PullRequestItem pr, CancellationToken ct)
    {
        try
        {
            var artifactId = $"vstfs:///CodeReview/CodeReviewId/{_projectId}/{pr.PullRequestId}";
            var url = $"{_baseUrl}/_apis/policy/evaluations?artifactId={artifactId}&api-version=7.0-preview";
            Log.Info($"GET {url}");
            var resp = await _http.GetAsync(url, ct);
            Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.Error($"  Body: {body}");
                return;
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var checks = new List<PipelineCheck>();
            foreach (var eval in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                // Only look at build validation policies
                var configType = "";
                var configName = "";
                if (eval.TryGetProperty("configuration", out var config))
                {
                    if (config.TryGetProperty("type", out var t) && t.TryGetProperty("displayName", out var dn))
                        configType = dn.GetString() ?? "";
                    if (config.TryGetProperty("settings", out var s) && s.TryGetProperty("displayName", out var sdn))
                        configName = sdn.GetString() ?? "";
                }

                if (!configType.Contains("Build", StringComparison.OrdinalIgnoreCase)) continue;

                var status = eval.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                Log.Info($"    Policy: {configName} ({configType}) = {status}");

                checks.Add(new PipelineCheck
                {
                    Name = configName,
                    State = status,
                    Description = $"{configType}: {status}",
                });
            }

            pr.Checks = checks;

            if (checks.Count == 0)
            {
                pr.PipelineStatus = PipelineState.None;
            }
            else if (checks.Any(c => c.State is "rejected" or "broken"))
            {
                pr.PipelineStatus = PipelineState.Failed;
            }
            else if (checks.Any(c => c.State is "running" or "queued"))
            {
                pr.PipelineStatus = PipelineState.Running;
            }
            else if (checks.All(c => c.State is "approved"))
            {
                pr.PipelineStatus = PipelineState.Succeeded;
            }
            else
            {
                pr.PipelineStatus = PipelineState.Running;
            }

            Log.Info($"  PR #{pr.PullRequestId} pipeline: {pr.PipelineStatus} ({checks.Count} build checks)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"FetchPolicyEvals PR #{pr.PullRequestId}: {ex.Message}");
        }
    }

    // ── Build / Pipeline APIs ────────────────────────────────────────

    public async Task<List<PipelineDefinition>> GetBuildDefinitionsAsync(CancellationToken ct = default)
    {
        var result = new List<PipelineDefinition>();
        try
        {
            var url = $"{_baseUrl}/_apis/build/definitions?api-version=7.0";
            Log.Info($"GET {url}");
            var resp = await _http.GetAsync(url, ct);
            Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!resp.IsSuccessStatusCode) return result;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var def in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                result.Add(new PipelineDefinition
                {
                    Id = def.GetProperty("id").GetInt32(),
                    Name = def.GetProperty("name").GetString() ?? "",
                    Path = def.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                });
            }

            Log.Info($"  Definitions: {result.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error($"GetBuildDefinitions: {ex.Message}"); }
        return result;
    }

    public async Task<List<BuildItem>> GetMyRecentFailedBuildsAsync(string userId, DateTime since, CancellationToken ct = default)
    {
        var result = new List<BuildItem>();
        try
        {
            var minTime = since.ToString("O");
            var url = $"{_baseUrl}/_apis/build/builds?requestedFor={Uri.EscapeDataString(userId)}"
                      + $"&statusFilter=completed&resultFilter=failed"
                      + $"&minTime={Uri.EscapeDataString(minTime)}&api-version=7.0";
            Log.Info($"GET {url}");
            var resp = await _http.GetAsync(url, ct);
            Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!resp.IsSuccessStatusCode) return result;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            result = ParseBuilds(doc);
            Log.Info($"  My failed builds since {minTime}: {result.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error($"GetMyRecentFailedBuilds: {ex.Message}"); }
        return result;
    }

    public async Task<List<BuildItem>> GetLatestBuildsForDefinitionsAsync(List<int> definitionIds, CancellationToken ct = default)
    {
        var result = new List<BuildItem>();
        if (definitionIds.Count == 0) return result;
        try
        {
            var ids = string.Join(",", definitionIds);
            var url = $"{_baseUrl}/_apis/build/builds?definitions={ids}"
                      + "&maxBuildsPerDefinition=1&statusFilter=completed&api-version=7.0";
            Log.Info($"GET {url}");
            var resp = await _http.GetAsync(url, ct);
            Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!resp.IsSuccessStatusCode) return result;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            result = ParseBuilds(doc);
            Log.Info($"  Latest builds for {definitionIds.Count} definitions: {result.Count}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error($"GetLatestBuildsForDefinitions: {ex.Message}"); }
        return result;
    }

    /// <summary>
    /// Gets the most recent build (any status, including in-progress) for each definition.
    /// Used to detect if a failure has been superseded by a newer success or retry.
    /// </summary>
    public async Task<List<(int DefinitionId, int BuildId, string Status, BuildResult Result, string WebUrl)>> GetLatestBuildStatusPerDefinitionAsync(
        IEnumerable<int> definitionIds, CancellationToken ct = default)
    {
        var result = new List<(int, int, string, BuildResult, string)>();
        var ids = string.Join(",", definitionIds);
        if (string.IsNullOrEmpty(ids)) return result;
        try
        {
            // No statusFilter → returns builds in any state (queued, inProgress, completed)
            var url = $"{_baseUrl}/_apis/build/builds?definitions={ids}"
                      + "&maxBuildsPerDefinition=1&queryOrder=queueTimeDescending&api-version=7.0";
            Log.Info($"GET {url}");
            var resp = await _http.GetAsync(url, ct);
            Log.Info($"  → {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!resp.IsSuccessStatusCode) return result;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var b in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var defId = 0;
                if (b.TryGetProperty("definition", out var def))
                    defId = def.TryGetProperty("id", out var did) ? did.GetInt32() : 0;

                var buildId = b.GetProperty("id").GetInt32();
                var status = b.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                var resultStr = b.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
                var buildResult = resultStr switch
                {
                    "succeeded" => BuildResult.Succeeded,
                    "partiallySucceeded" => BuildResult.PartiallySucceeded,
                    "failed" => BuildResult.Failed,
                    "canceled" => BuildResult.Canceled,
                    _ => BuildResult.None,
                };

                var webUrl = "";
                if (b.TryGetProperty("_links", out var links)
                    && links.TryGetProperty("web", out var web)
                    && web.TryGetProperty("href", out var href))
                    webUrl = href.GetString() ?? "";

                result.Add((defId, buildId, status, buildResult, webUrl));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error($"GetLatestBuildStatusPerDefinition: {ex.Message}"); }
        return result;
    }

    private static List<BuildItem> ParseBuilds(JsonDocument doc)
    {
        var items = new List<BuildItem>();
        foreach (var b in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var resultStr = b.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var buildResult = resultStr switch
            {
                "succeeded" => BuildResult.Succeeded,
                "partiallySucceeded" => BuildResult.PartiallySucceeded,
                "failed" => BuildResult.Failed,
                "canceled" => BuildResult.Canceled,
                _ => BuildResult.None,
            };

            var defName = "";
            var defId = 0;
            if (b.TryGetProperty("definition", out var def))
            {
                defName = def.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                defId = def.TryGetProperty("id", out var did) ? did.GetInt32() : 0;
            }

            var requestedBy = "";
            if (b.TryGetProperty("requestedFor", out var rf))
                requestedBy = rf.TryGetProperty("displayName", out var dn2) ? dn2.GetString() ?? "" : "";

            var webUrl = "";
            if (b.TryGetProperty("_links", out var links)
                && links.TryGetProperty("web", out var web)
                && web.TryGetProperty("href", out var href))
                webUrl = href.GetString() ?? "";

            items.Add(new BuildItem
            {
                Id = b.GetProperty("id").GetInt32(),
                DefinitionId = defId,
                DefinitionName = defName,
                Result = buildResult,
                SourceBranch = b.TryGetProperty("sourceBranch", out var sb) ? sb.GetString() ?? "" : "",
                RequestedBy = requestedBy,
                FinishTime = b.TryGetProperty("finishTime", out var ft) ? ft.GetDateTime() : DateTime.MinValue,
                WebUrl = webUrl,
            });
        }
        return items;
    }

    public void Dispose() => _http.Dispose();
}
