using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

/// <summary>One Sentry issue, as shown in the list and in notifications.</summary>
public sealed partial class SentryIssueItem : ObservableObject
{
    public string Id { get; init; } = "";

    /// <summary>Human readable identifier, of the form PROJECT-SLUG-4T4.</summary>
    public string ShortId { get; init; } = "";

    /// <summary>
    /// The short id without its project prefix, e.g. "4T4". The prefix only repeats the project,
    /// which the user picked and which is shown separately when it is ambiguous.
    /// </summary>
    public string ShortCode
    {
        get
        {
            string prefix = ProjectSlug.Replace('-', ' ').ToUpperInvariant().Replace(' ', '-') + "-";
            return ShortId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? ShortId[prefix.Length..]
                : ShortId;
        }
    }

    /// <summary>The one-line detail under the title, with redundant parts left out.</summary>
    public string Subtitle
    {
        get
        {
            List<string> parts = [ShortCode, Reach, RecentReach];
            if (Age.Length > 0) parts.Add(Age == "just now" ? "seen just now" : $"seen {Age} ago");
            return string.Join(" · ", parts);
        }
    }

    public string Title { get; init; } = "";

    /// <summary>Where it happened, often the failing request or frame.</summary>
    public string Culprit { get; init; } = "";

    public string Level { get; init; } = "";

    /// <summary>
    /// Fatal or error. A warning or info issue is a report the app chose to send, which is worth
    /// seeing but is not a crash, so it is listed apart and never ranked against one.
    /// </summary>
    public bool IsCrash => Level is "fatal" or "error" or "";

    public string ProjectSlug { get; init; } = "";

    /// <summary>
    /// The window <see cref="RecentEventCount"/> and <see cref="RecentUserCount"/> cover, and the
    /// one an issue must have been seen in to be listed at all. In Sentry's own period notation.
    /// </summary>
    public const string RecentWindow = "14d";

    /// <summary>Every event since Sentry first saw the issue, as Sentry itself shows it.</summary>
    public int EventCount { get; init; }

    /// <summary>Everyone the issue has ever reached, which is what ranks the list.</summary>
    public int UserCount { get; init; }

    /// <summary>Events within <see cref="RecentWindow"/>.</summary>
    public int RecentEventCount { get; init; }

    /// <summary>People reached within <see cref="RecentWindow"/>, which says whether it is still going.</summary>
    public int RecentUserCount { get; init; }

    /// <summary>Sentry's id of whoever the issue is assigned to, a user or a team. Empty when nobody.</summary>
    public string AssigneeId { get; init; } = "";

    public string AssigneeName { get; init; } = "";

    public bool AssigneeIsTeam { get; init; }

    public bool IsAssigned => AssigneeId.Length > 0;

    /// <summary>
    /// Assigned to someone, whoever it is, so it leaves the lists of what to pick up and waits with
    /// the muted ones, returning as an alert if it regresses or its recent reach doubles.
    /// </summary>
    public bool IsSetAside { get; set; }

    /// <summary>Who has it, and for one set aside, what brings it back. Empty when unassigned.</summary>
    public string AssignedLine
    {
        get
        {
            if (!IsAssigned) return "";
            string who = AssigneeIsTeam ? $"team {AssigneeName}" : AssigneeName;
            return IsSetAside
                ? $"assigned to {who} · back if its {RecentWindow.TrimEnd('d')}-day reach doubles or it regresses"
                : $"assigned to {who}";
        }
    }

    public bool HasAssignedLine => AssignedLine.Length > 0;

    /// <summary>When Sentry first saw the issue, not when it first appeared in the window.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; init; }

    public string Permalink { get; init; } = "";

    /// <summary>Sentry has seen this issue return after it was resolved.</summary>
    public bool IsRegression { get; init; }

    /// <summary>App version this issue was first seen in, empty until it has been looked up.</summary>
    public string FirstReleaseVersion { get; set; } = "";

    /// <summary>Newest app version this issue has been seen in. The first thing triage asks.</summary>
    public string LastReleaseVersion { get; set; } = "";

    /// <summary>Versions affected, collapsed to one when it is only ever been seen in one.</summary>
    public string ReleaseSummary => (FirstReleaseVersion, LastReleaseVersion) switch
    {
        ("", "") => "",
        ("", var last) => $"in {last}",
        (var first, "") => $"in {first}",
        (var first, var last) when first == last => $"in {first}",
        (var first, var last) => $"{first} → {last}",
    };

    public bool HasRelease => !string.IsNullOrEmpty(ReleaseSummary);

    /// <summary>Events per bucket over the last 24 hours, oldest first.</summary>
    public IReadOnlyList<double> EventSeries { get; init; } = [];

    /// <summary>How long one entry in <see cref="EventSeries"/> covers.</summary>
    public int SeriesBucketMinutes { get; init; }

    /// <summary>Sparkline of the event counts, as block characters.</summary>
    public string Spark => Trend.Spark(EventSeries);

    /// <summary>The shape in a phrase: "constant", "one burst 3h ago", "easing off".</summary>
    public string TrendVerdict => Trend.DescribeCounts(EventSeries, SeriesBucketMinutes);

    /// <summary>
    /// Only once there is enough volume to say something. A series of one event renders as a flat
    /// line with a single blip, which reads as a stray underline rather than as a shape.
    /// </summary>
    public bool HasTrend => TrendVerdict.Length > 0;

    /// <summary>
    /// Huginn raised this and the user has not dismissed it, so it stays at the top of the list.
    /// </summary>
    public bool IsFlagged { get; set; }

    /// <summary>Why it was raised: "new", "regressed" or "worse".</summary>
    public string FlagReason { get; set; } = "";

    /// <summary>Dismiss clears a stored alert, so there is something to dismiss only while raised.</summary>
    public bool CanDismiss { get; private set; }

    /// <summary>
    /// Settles the raised state from the stored alert. Both the poll and a repaint after a mute go
    /// through here, so they cannot disagree about what is raised.
    /// </summary>
    public void ApplyRaise(bool alerted, string alertReason)
    {
        CanDismiss = alerted;
        IsFlagged = alerted;
        FlagReason = alerted ? alertReason : "";
    }

    public bool HasFlagReason => FlagReason.Length > 0;

    /// <summary>
    /// People reached in <see cref="RecentWindow"/> when it was muted, so the card can show what
    /// it has grown from.
    /// </summary>
    public int MutedAtUserCount { get; set; }

    /// <summary>User has acknowledged it, so it stays listed but leaves the badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteTooltip))]
    [NotifyPropertyChangedFor(nameof(MuteLabel))]
    [NotifyPropertyChangedFor(nameof(MuteIcon))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    private bool _isMuted;

    public string MuteTooltip => IsMuted
        ? "Unmute, so this issue is listed and alerts normally again"
        : $"Mute at its current size. It comes back if it regresses or its reach in {RecentWindow} roughly doubles.";

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    /// <summary>The label as a glyph. Card rows are narrow, and the tooltip carries the words.</summary>
    public string MuteIcon => IsMuted ? "🔊" : "🔇";

    /// <summary>Muted issues sit in the same list, told apart by being visibly quieter.</summary>
    public double CardOpacity => IsMuted ? 0.55 : 1.0;

    public string LevelIcon => Level switch
    {
        "fatal" => "💀",
        "error" => "❌",
        "warning" => "⚠️",
        "info" => "ℹ️",
        _ => "❓",
    };

    /// <summary>
    /// Everyone reached and since when, e.g. "1,353 users since Apr 14". A crash that has been
    /// around for months reads very differently from one that appeared this morning.
    /// </summary>
    public string Reach
    {
        get
        {
            string users = UserCount == 1 ? "1 user" : $"{UserCount.ToString("N0", English)} users";
            return FirstSeen == DateTimeOffset.MinValue ? users : $"{users} since {Day(FirstSeen)}";
        }
    }

    /// <summary>People reached lately, e.g. "40 in 14d".</summary>
    public string RecentReach => $"{RecentUserCount.ToString("N0", English)} in {RecentWindow}";

    /// <summary>Reach in full, for a notification, which has room the card does not.</summary>
    public string Impact =>
        $"{Reach}, {RecentReach} · {EventCount.ToString("N0", English)} events";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>A date as en-US writes it, with the year only when it is not this one.</summary>
    private static string Day(DateTimeOffset at)
    {
        DateTime local = at.ToLocalTime().DateTime;
        return local.Year == DateTime.Now.Year
            ? local.ToString("MMM d", English)
            : local.ToString("MMM d, yyyy", English);
    }

    /// <summary>How long ago it last happened, which says whether it is still going.</summary>
    public string Age => LastSeen == DateTimeOffset.MinValue ? "" : Elapsed(LastSeen);

    private static string Elapsed(DateTimeOffset since)
    {
        TimeSpan span = DateTimeOffset.UtcNow - since;
        if (span.TotalDays >= 365) return $"{(int)(span.TotalDays / 365)}y";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return "just now";
    }
}
