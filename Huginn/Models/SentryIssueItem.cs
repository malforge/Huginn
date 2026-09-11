using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

/// <summary>One Sentry issue, as shown in the list and in notifications.</summary>
public sealed partial class SentryIssueItem : ObservableObject
{
    public string Id { get; init; } = "";

    /// <summary>Human readable identifier, e.g. "POWEROFFICE-GO-4T4".</summary>
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
            List<string> parts = [ShortCode, Impact];
            if (TotalAge.Length > 0) parts.Add($"{TotalAge} old");
            if (Age.Length > 0) parts.Add(Age == "just now" ? "seen just now" : $"seen {Age} ago");
            return string.Join(" · ", parts);
        }
    }

    public string Title { get; init; } = "";

    /// <summary>Where it happened, often the failing request or frame.</summary>
    public string Culprit { get; init; } = "";

    public string Level { get; init; } = "";

    public string ProjectSlug { get; init; } = "";

    public int EventCount { get; init; }

    public int UserCount { get; init; }

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

    /// <summary>Events per bucket over the stats window, oldest first.</summary>
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

    /// <summary>Why it was raised: "new", "regressed", "worse" or "widespread".</summary>
    public string FlagReason { get; set; } = "";

    /// <summary>
    /// Reaching enough people to matter on its own. Current state rather than a stored alert, so
    /// it is recomputed every poll and goes away again when the issue quietens.
    /// </summary>
    public bool IsWidespread { get; set; }

    /// <summary>
    /// Dismiss clears a stored alert. Reach is recomputed from the next poll, so there is nothing
    /// for it to clear and the button would do nothing.
    /// </summary>
    public bool CanDismiss { get; private set; }

    /// <summary>
    /// Settles the raised state from the stored alert and from live reach. Both the poll and a
    /// repaint after a mute go through here, so they cannot disagree about what is raised.
    /// </summary>
    public void ApplyRaise(bool alerted, string alertReason)
    {
        CanDismiss = alerted;
        IsFlagged = alerted || IsWidespread;
        FlagReason = alerted ? alertReason : IsWidespread ? "widespread" : "";
    }

    public bool HasFlagReason => FlagReason.Length > 0;

    /// <summary>User count when it was muted, so the card can show what it has grown from.</summary>
    public int MutedAtUserCount { get; set; }

    /// <summary>User has acknowledged it, so it stays listed but leaves the badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteTooltip))]
    [NotifyPropertyChangedFor(nameof(MuteLabel))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    private bool _isMuted;

    public string MuteTooltip => IsMuted
        ? "Unmute, so this issue is listed and alerts normally again"
        : "Mute at its current size. It comes back if it regresses or roughly doubles.";

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

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

    /// <summary>Reach, which is what usually decides whether an issue matters.</summary>
    public string Impact => UserCount == 1
        ? $"{EventCount:N0} events · 1 user"
        : $"{EventCount:N0} events · {UserCount:N0} users";

    /// <summary>
    /// How long the issue has existed. A crash that has been around for weeks reads very
    /// differently from one that appeared this morning, even when both fired a minute ago.
    /// </summary>
    public string TotalAge => FirstSeen == DateTimeOffset.MinValue ? "" : Elapsed(FirstSeen);

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
