using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // Sentry
    public string SentryOrganization { get; set; } = "";
    public string SentryRegionUrl { get; set; } = "https://sentry.io";
    public List<string> WatchedSentryProjects { get; set; } = [];

    // Application Insights
    public string AppInsightsTenantId { get; set; } = "";

    /// <summary>
    /// Application registration presented at sign-in. Empty uses the Azure SDK development
    /// application, which needs no setup but is not the recommended posture for a production tenant.
    /// </summary>
    public string AppInsightsClientId { get; set; } = "";

    public List<string> WatchedAppInsightsAppIds { get; set; } = [];

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
    public bool IsAppInsightsConfigured => WatchedAppInsightsAppIds.Count > 0;

    /// <summary>The Sentry connection has everything it needs to attempt a poll.</summary>
    public bool IsSentryConfigured =>
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
            SentryOrganization = SentryOrganization,
            SentryRegionUrl = SentryRegionUrl,
            WatchedSentryProjects = WatchedSentryProjects,
            AppInsightsTenantId = AppInsightsTenantId,
            AppInsightsClientId = AppInsightsClientId,
            WatchedAppInsightsAppIds = WatchedAppInsightsAppIds,
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
                SentryOrganization = dto.SentryOrganization ?? "",
                SentryRegionUrl = string.IsNullOrWhiteSpace(dto.SentryRegionUrl)
                    ? "https://sentry.io"
                    : dto.SentryRegionUrl,
                WatchedSentryProjects = dto.WatchedSentryProjects ?? [],
                AppInsightsTenantId = dto.AppInsightsTenantId ?? "",
                AppInsightsClientId = dto.AppInsightsClientId ?? "",
                WatchedAppInsightsAppIds = dto.WatchedAppInsightsAppIds ?? [],
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
        public string? SentryOrganization { get; set; }
        public string? SentryRegionUrl { get; set; }
        public List<string>? WatchedSentryProjects { get; set; }
        public string? AppInsightsTenantId { get; set; }
        public string? AppInsightsClientId { get; set; }
        public List<string>? WatchedAppInsightsAppIds { get; set; }
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
