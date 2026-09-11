using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Huginn.Models;

namespace Huginn.Services;

public sealed partial class AppSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Huginn");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private const string CredentialTarget = "Huginn:ADO:PAT";
    private const string SentryCredentialTarget = "Huginn:Sentry:Token";

    private readonly ICredentialStore _credentialStore;
    private string? _cachedPat;
    private string? _cachedSentryToken;

    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Sentry is polled less often than Azure DevOps. Each poll re-reads the same window and then
    /// pays a throttled request per issue for its version, so a faster cadence buys nothing.
    /// </summary>
    public int SentryPollIntervalMinutes { get; set; } = 10;

    // Sentry
    public string SentryOrganization { get; set; } = "";
    public string SentryRegionUrl { get; set; } = "https://sentry.io";
    public List<string> WatchedSentryProjects { get; set; } = [];

    /// <summary>
    /// Sentry issues the user has muted, keyed by issue id, holding how big each was at the time.
    /// </summary>
    public Dictionary<string, SentryAcknowledgement> MutedSentryIssues { get; set; } = [];

    /// <summary>
    /// Issues Huginn has raised and the user has not dismissed, keyed by issue id with the reason
    /// it was raised. Persisted, because an alert the user never saw must survive a restart.
    /// </summary>
    public Dictionary<string, string> FlaggedSentryIssues { get; set; } = [];

    /// <summary>
    /// Service findings the user has muted, keyed by finding id, holding how bad each was at the
    /// time. Kept separate from the Sentry equivalents rather than unified, because renaming
    /// persisted keys would silently discard whatever is already muted.
    /// </summary>
    public Dictionary<string, double> MutedFindings { get; set; } = [];

    /// <summary>Findings raised and not yet dismissed, keyed by finding id with the reason.</summary>
    public Dictionary<string, string> FlaggedFindings { get; set; } = [];

    /// <summary>How often Application Insights is examined. These are aggregate queries over a
    /// window, so a faster cadence mostly re-reads the same numbers.</summary>
    public int AppInsightsPollIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// How far back each rule looks. Short reacts fast but is noisy on low traffic; long is
    /// steady but slow to notice. There is no right answer, so it is yours to set.
    /// </summary>
    public int AppInsightsWindowMinutes { get; set; } = 60;

    // Application Insights
    public string AppInsightsTenantId { get; set; } = "";

    /// <summary>
    /// Application registration presented at sign-in. Empty uses the Azure SDK development
    /// application, which needs no setup but is not the recommended posture for a production tenant.
    /// </summary>
    public string AppInsightsClientId { get; set; } = "";

    /// <summary>
    /// Watched resources, keyed by query app id. The value is the full ARM id where it is known,
    /// and a bare resource name for entries saved before the id was kept.
    /// </summary>
    public Dictionary<string, string> WatchedAppInsights { get; set; } = [];

    /// <summary>Resource name for a watched entry, whichever form the value takes.</summary>
    public static string NameOf(string value) =>
        value.StartsWith('/') ? value[(value.LastIndexOf('/') + 1)..] : value;

    /// <summary>ARM id for a watched entry, empty when only the name was stored.</summary>
    public static string ResourceIdOf(string value) => value.StartsWith('/') ? value : "";

    // Window state (only size/position are recorded when in Normal state)
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int WindowState { get; set; } // 0=Normal, 1=Minimized, 2=Maximized

    // Pipeline monitoring
    public List<int> WatchedPipelineIds { get; set; } = [];
    public bool MonitorMyBuilds { get; set; } = true;

    // Acknowledgements (build IDs and PR IDs that the user has dismissed from the badge).
    // A new build run gets a new ID, so it naturally re-appears.
    public List<int> AcknowledgedBuildIds { get; set; } = [];
    public List<int> AcknowledgedPrIds { get; set; } = [];

    public AppSettings(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    /// <summary>The Azure DevOps connection has everything it needs to attempt a poll.</summary>
    public bool IsAdoConfigured =>
        !string.IsNullOrWhiteSpace(Organization)
        && !string.IsNullOrWhiteSpace(Project)
        && !string.IsNullOrWhiteSpace(GetPat());

    /// <summary>The Application Insights connection has a signed-in account and something to watch.</summary>
    public bool IsAppInsightsConfigured => WatchedAppInsights.Count > 0;

    /// <summary>The Sentry connection has everything it needs to attempt a poll.</summary>
    public bool IsSentryConfigured =>
        !string.IsNullOrWhiteSpace(SentryOrganization)
        && !string.IsNullOrWhiteSpace(GetSentryToken())
        && WatchedSentryProjects.Count > 0;

    /// <summary>Credentials are present, whether or not anything has been picked to watch.</summary>
    public bool HasSentryCredentials =>
        !string.IsNullOrWhiteSpace(SentryOrganization)
        && !string.IsNullOrWhiteSpace(GetSentryToken());

    public string? GetPat()
    {
        _cachedPat ??= _credentialStore.Get(CredentialTarget);
        return _cachedPat;
    }

    public bool SetPat(string pat)
    {
        _cachedPat = pat;
        return _credentialStore.Set(CredentialTarget, pat, "ADO PAT");
    }

    public string? GetSentryToken()
    {
        _cachedSentryToken ??= _credentialStore.Get(SentryCredentialTarget);
        return _cachedSentryToken;
    }

    public bool SetSentryToken(string token)
    {
        _cachedSentryToken = token;
        return _credentialStore.Set(SentryCredentialTarget, token, "Sentry token");
    }

    /// <summary>Base URL of the Sentry API, honouring the organisation data region.</summary>
    public string GetSentryApiBaseUrl() => $"{SentryRegionUrl.TrimEnd('/')}/api/0";

    /// <summary>
    /// Auth tokens are account level rather than organisation level, so this needs only the data
    /// region and is always openable.
    /// </summary>
    public string GetSentryTokenPageUrl()
    {
        string region = string.IsNullOrWhiteSpace(SentryRegionUrl)
            ? "https://sentry.io"
            : SentryRegionUrl.TrimEnd('/');
        return $"{region}/settings/account/api/auth-tokens/";
    }

    public string GetWebBaseUrl() =>
        $"https://dev.azure.com/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}";

    public bool IsBuildAcknowledged(int id) => AcknowledgedBuildIds.Contains(id);
    public bool IsPrAcknowledged(int id) => AcknowledgedPrIds.Contains(id);

    public void SetBuildAcknowledged(int id, bool ack)
    {
        bool changed;
        if (ack)
        {
            changed = !AcknowledgedBuildIds.Contains(id);
            if (changed) AcknowledgedBuildIds.Add(id);
        }
        else
        {
            changed = AcknowledgedBuildIds.Remove(id);
        }
        if (changed) Save();
    }

    public void SetPrAcknowledged(int id, bool ack)
    {
        bool changed;
        if (ack)
        {
            changed = !AcknowledgedPrIds.Contains(id);
            if (changed) AcknowledgedPrIds.Add(id);
        }
        else
        {
            changed = AcknowledgedPrIds.Remove(id);
        }
        if (changed) Save();
    }

    /// <summary>
    /// Mutes a Sentry issue at its current size. It stays muted until it regresses or roughly
    /// doubles, so an unfixable crash goes quiet without going silent for good.
    /// </summary>
    public void MuteSentryIssue(string issueId, int userCount, int eventCount)
    {
        MutedSentryIssues[issueId] = new SentryAcknowledgement
        {
            UserCount = userCount,
            EventCount = eventCount,
        };
        Save();
    }

    public void UnmuteSentryIssue(string issueId)
    {
        if (MutedSentryIssues.Remove(issueId)) Save();
    }

    /// <summary>Factor by which a muted issue must grow before it is worth raising again.</summary>
    private const int EscalationFactor = 2;

    /// <summary>
    /// True when a muted issue has grown enough, or returned, that the mute should be lifted.
    /// </summary>
    public bool HasOutgrownMute(string issueId, int userCount, int eventCount, bool isRegression)
    {
        if (!MutedSentryIssues.TryGetValue(issueId, out SentryAcknowledgement? muted)) return false;
        if (isRegression) return true;

        // Guard the zero case: something muted at no users escalates on its first user.
        if (muted.UserCount == 0 && muted.EventCount == 0) return userCount > 0 || eventCount > 0;

        return userCount >= muted.UserCount * EscalationFactor
            || eventCount >= muted.EventCount * EscalationFactor;
    }

    public bool IsSentryIssueMuted(string issueId) => MutedSentryIssues.ContainsKey(issueId);

    /// <summary>Raises an issue so it stays at the top of the list until it is dismissed.</summary>
    public void FlagSentryIssue(string issueId, string reason)
    {
        // An issue already raised keeps its original reason: "new" then "worse" is still new.
        if (FlaggedSentryIssues.ContainsKey(issueId)) return;
        FlaggedSentryIssues[issueId] = reason;
        Save();
    }

    public void DismissSentryIssue(string issueId)
    {
        if (FlaggedSentryIssues.Remove(issueId)) Save();
    }

    public bool IsSentryIssueFlagged(string issueId) => FlaggedSentryIssues.ContainsKey(issueId);

    public string GetSentryFlagReason(string issueId) =>
        FlaggedSentryIssues.TryGetValue(issueId, out string? reason) ? reason : "";

    public void MuteFinding(string id, double magnitude)
    {
        MutedFindings[id] = magnitude;
        Save();
    }

    public void UnmuteFinding(string id)
    {
        if (MutedFindings.Remove(id)) Save();
    }

    public bool IsFindingMuted(string id) => MutedFindings.ContainsKey(id);

    public double GetFindingMutedAt(string id) =>
        MutedFindings.TryGetValue(id, out double at) ? at : 0;

    /// <summary>True when a muted finding has grown enough to be worth raising again.</summary>
    public bool HasFindingOutgrownMute(string id, double magnitude, double factor)
    {
        if (!MutedFindings.TryGetValue(id, out double muted)) return false;
        return muted <= 0 ? magnitude > 0 : magnitude >= muted * factor;
    }

    public void FlagFinding(string id, string reason)
    {
        if (FlaggedFindings.ContainsKey(id)) return;
        FlaggedFindings[id] = reason;
        Save();
    }

    public void DismissFinding(string id)
    {
        if (FlaggedFindings.Remove(id)) Save();
    }

    public bool IsFindingFlagged(string id) => FlaggedFindings.ContainsKey(id);

    public string GetFindingFlagReason(string id) =>
        FlaggedFindings.TryGetValue(id, out string? reason) ? reason : "";

    /// <summary>Drops flags for findings that no longer appear.</summary>
    public void PruneFindingFlags(HashSet<string> alive)
    {
        List<string> gone = FlaggedFindings.Keys.Where(id => !alive.Contains(id)).ToList();
        foreach (string id in gone) FlaggedFindings.Remove(id);
        if (gone.Count > 0) Save();
    }

    /// <summary>Drops flags for issues that no longer come back from Sentry at all.</summary>
    public void PruneSentryFlags(HashSet<string> aliveIssueIds)
    {
        List<string> gone = FlaggedSentryIssues.Keys.Where(id => !aliveIssueIds.Contains(id)).ToList();
        foreach (string id in gone) FlaggedSentryIssues.Remove(id);
        if (gone.Count > 0) Save();
    }

    /// <summary>Drop acknowledgements for items that no longer appear in poll results.</summary>
    public void PruneAcknowledgements(HashSet<int> aliveBuildIds, HashSet<int> alivePrIds)
    {
        var removed = AcknowledgedBuildIds.RemoveAll(id => !aliveBuildIds.Contains(id));
        removed += AcknowledgedPrIds.RemoveAll(id => !alivePrIds.Contains(id));
        if (removed > 0) Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(new SettingsDto
        {
            Organization = Organization,
            Project = Project,
            PollIntervalMinutes = PollIntervalMinutes,
            SentryPollIntervalMinutes = SentryPollIntervalMinutes,
            SentryOrganization = SentryOrganization,
            SentryRegionUrl = SentryRegionUrl,
            WatchedSentryProjects = WatchedSentryProjects,
            MutedSentryIssues = MutedSentryIssues,
            FlaggedSentryIssues = FlaggedSentryIssues,
            MutedFindings = MutedFindings,
            FlaggedFindings = FlaggedFindings,
            AppInsightsPollIntervalMinutes = AppInsightsPollIntervalMinutes,
            AppInsightsWindowMinutes = AppInsightsWindowMinutes,
            AppInsightsTenantId = AppInsightsTenantId,
            AppInsightsClientId = AppInsightsClientId,
            WatchedAppInsights = WatchedAppInsights,
            WindowX = WindowX,
            WindowY = WindowY,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            WindowState = WindowState,
            WatchedPipelineIds = WatchedPipelineIds,
            MonitorMyBuilds = MonitorMyBuilds,
            AcknowledgedBuildIds = AcknowledgedBuildIds,
            AcknowledgedPrIds = AcknowledgedPrIds,
        }, SettingsJsonContext.Default.SettingsDto);
        File.WriteAllText(SettingsPath, json);
    }

    public static AppSettings Load(ICredentialStore credentialStore)
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings(credentialStore);

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var dto = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDto);
            if (dto == null) return new AppSettings(credentialStore);

            return new AppSettings(credentialStore)
            {
                Organization = dto.Organization ?? "",
                Project = dto.Project ?? "",
                PollIntervalMinutes = dto.PollIntervalMinutes > 0 ? dto.PollIntervalMinutes : 5,
                SentryPollIntervalMinutes =
                    dto.SentryPollIntervalMinutes > 0 ? dto.SentryPollIntervalMinutes : 10,
                SentryOrganization = dto.SentryOrganization ?? "",
                SentryRegionUrl = string.IsNullOrWhiteSpace(dto.SentryRegionUrl)
                    ? "https://sentry.io"
                    : dto.SentryRegionUrl,
                WatchedSentryProjects = dto.WatchedSentryProjects ?? [],
                MutedSentryIssues = dto.MutedSentryIssues ?? [],
                FlaggedSentryIssues = dto.FlaggedSentryIssues ?? [],
                MutedFindings = dto.MutedFindings ?? [],
                FlaggedFindings = dto.FlaggedFindings ?? [],
                AppInsightsPollIntervalMinutes =
                    dto.AppInsightsPollIntervalMinutes > 0 ? dto.AppInsightsPollIntervalMinutes : 15,
                AppInsightsWindowMinutes =
                    dto.AppInsightsWindowMinutes > 0 ? dto.AppInsightsWindowMinutes : 60,
                AppInsightsTenantId = dto.AppInsightsTenantId ?? "",
                AppInsightsClientId = dto.AppInsightsClientId ?? "",
                WatchedAppInsights = dto.WatchedAppInsights ?? [],
                WindowX = dto.WindowX,
                WindowY = dto.WindowY,
                WindowWidth = dto.WindowWidth,
                WindowHeight = dto.WindowHeight,
                WindowState = dto.WindowState,
                WatchedPipelineIds = dto.WatchedPipelineIds ?? [],
                MonitorMyBuilds = dto.MonitorMyBuilds,
                AcknowledgedBuildIds = dto.AcknowledgedBuildIds ?? [],
                AcknowledgedPrIds = dto.AcknowledgedPrIds ?? [],
            };
        }
        catch
        {
            return new AppSettings(credentialStore);
        }
    }

    private sealed class SettingsDto
    {
        public string? Organization { get; set; }
        public string? Project { get; set; }
        public int PollIntervalMinutes { get; set; }
        public int SentryPollIntervalMinutes { get; set; } = 10;
        public string? SentryOrganization { get; set; }
        public string? SentryRegionUrl { get; set; }
        public List<string>? WatchedSentryProjects { get; set; }
        public Dictionary<string, SentryAcknowledgement>? MutedSentryIssues { get; set; }
        public Dictionary<string, string>? FlaggedSentryIssues { get; set; }
        public Dictionary<string, double>? MutedFindings { get; set; }
        public Dictionary<string, string>? FlaggedFindings { get; set; }
        public int AppInsightsPollIntervalMinutes { get; set; } = 15;
        public int AppInsightsWindowMinutes { get; set; } = 60;
        public string? AppInsightsTenantId { get; set; }
        public string? AppInsightsClientId { get; set; }
        public Dictionary<string, string>? WatchedAppInsights { get; set; }
        public double? WindowX { get; set; }
        public double? WindowY { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public int WindowState { get; set; }
        public List<int>? WatchedPipelineIds { get; set; }
        public bool MonitorMyBuilds { get; set; } = true;
        public List<int>? AcknowledgedBuildIds { get; set; }
        public List<int>? AcknowledgedPrIds { get; set; }
    }

    [JsonSerializable(typeof(SettingsDto))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    private sealed partial class SettingsJsonContext : JsonSerializerContext;
}
