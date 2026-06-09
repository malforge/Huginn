using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Velopack;
using Velopack.Sources;

namespace Huginn.Services.Updates;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    UpdateReady,
    Failed
}

/// <summary>
/// Singleton wrapper around Velopack's UpdateManager. Holds observable state
/// (current/available version, status) for ViewModels to bind to. All property
/// change notifications are marshalled to the UI thread so background update
/// checks don't break bindings.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public static UpdateService Instance { get; } = new();

    private const string GithubOwner = "malforge";
    private const string GithubRepo = "Huginn";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Huginn-Updater");
        return http;
    }

    private static readonly TimeSpan BackgroundCheckInterval = TimeSpan.FromMinutes(30);

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pendingUpdate;
    private DispatcherTimer? _pollTimer;

    [ObservableProperty] private UpdateState _state = UpdateState.Idle;
    [ObservableProperty] private string _currentVersion = "0.0.0";
    [ObservableProperty] private string? _availableVersion;
    [ObservableProperty] private string? _releaseNotesMarkdown;
    [ObservableProperty] private string? _errorMessage;

    private UpdateService()
    {
        _mgr = new UpdateManager(new GithubSource(
            $"https://github.com/{GithubOwner}/{GithubRepo}",
            accessToken: null,
            prerelease: false));

        // Velopack's CurrentVersion is the installed-package version; it's null
        // on dev/local-publish builds. Fall back to the assembly's informational
        // version, which MSBuild derives from <Version> in the csproj — which is
        // read from PackageVersion.txt — so the two paths agree. Strip SemVer
        // build metadata (`+<commit-sha>`) appended by the SDK in git checkouts.
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0];
        CurrentVersion = _mgr.CurrentVersion?.ToString() ?? informational ?? "0.0.0";
    }

    /// <summary>
    /// True when the running process can actually apply updates (installed via
    /// the Velopack installer). False on dev/local-publish builds — checks
    /// no-op in that case and the UI shows nothing.
    /// </summary>
    public bool CanUpdate => _mgr.IsInstalled;

    /// <summary>
    /// Starts wall-clock polling for updates: one check immediately, then another
    /// every <see cref="BackgroundCheckInterval"/> for as long as the app runs.
    /// Idempotent. Polling is independent of user activity, so a release published
    /// while the app is open is still picked up on its own.
    /// </summary>
    public void StartPolling()
    {
        if (_pollTimer is not null) return;

        _pollTimer = new DispatcherTimer { Interval = BackgroundCheckInterval };
        _pollTimer.Tick += (_, _) => RequestBackgroundCheck();
        _pollTimer.Start();

        RequestBackgroundCheck();
    }

    /// <summary>
    /// Fire-and-forget update check on a background thread; observe the result via
    /// State/AvailableVersion. Driven by <see cref="StartPolling"/>'s timer. Use
    /// CheckAsync when you need to await the result, e.g. from a manual "Check now" button.
    /// </summary>
    public void RequestBackgroundCheck() => _ = Task.Run(CheckAsync);

    /// <summary>
    /// Awaitable update check. No-ops on dev/local-publish builds where Velopack
    /// is not installed (CanUpdate=false); State remains Idle in that case.
    /// </summary>
    public async Task CheckAsync()
    {
        if (!_mgr.IsInstalled) return;

        try
        {
            State = UpdateState.Checking;
            ErrorMessage = null;

            var update = await _mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                _pendingUpdate = null;
                AvailableVersion = null;
                ReleaseNotesMarkdown = null;
                State = UpdateState.UpToDate;
                return;
            }

            _pendingUpdate = update;
            AvailableVersion = update.TargetFullRelease.Version?.ToString();

            await _mgr.DownloadUpdatesAsync(update);
            State = UpdateState.UpdateReady;

            _ = Task.Run(FetchReleaseNotesAsync);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = UpdateState.Failed;
        }
    }

    private async Task FetchReleaseNotesAsync()
    {
        var version = AvailableVersion;
        if (string.IsNullOrEmpty(version)) return;
        try
        {
            var url = $"https://api.github.com/repos/{GithubOwner}/{GithubRepo}/releases/tags/v{version}";
            using var response = await HttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                ReleaseNotesMarkdown = body.GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or System.Threading.Tasks.TaskCanceledException)
        {
            // Best-effort — UI falls back to no release notes.
        }
    }

    /// <summary>
    /// Applies the downloaded update and restarts the app. No-ops if no update
    /// has been downloaded yet (State must be UpdateReady).
    ///
    /// Uses <c>WaitExitThenApplyUpdates</c> + a graceful Avalonia shutdown rather
    /// than <c>UpdateManager.ApplyUpdatesAndRestart</c> — the latter calls
    /// <c>Environment.Exit</c> right after spawning Update.exe, bypassing
    /// Avalonia's window-close lifecycle (and any ViewModel Dispose hooks).
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_pendingUpdate is null) return;

        _mgr.WaitExitThenApplyUpdates(_pendingUpdate, silent: false, restart: true);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            Dispatcher.UIThread.Post(() => lifetime.Shutdown());
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            base.OnPropertyChanged(e);
        else
            Dispatcher.UIThread.Post(() => base.OnPropertyChanged(e));
    }
}
