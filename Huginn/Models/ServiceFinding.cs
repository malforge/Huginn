using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

/// <summary>
/// One problem detected on one Application Insights resource, such as a failing route or a
/// dependency that has started erroring.
/// </summary>
public sealed partial class ServiceFinding : ObservableObject
{
    public FindingKind Kind { get; init; }

    /// <summary>The resource this was found on.</summary>
    public string ResourceName { get; init; } = "";

    public string AppId { get; init; } = "";

    /// <summary>ARM id of the resource, used to build the portal link.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>
    /// Opens the portal’s Logs view on the query that explains this finding, so the link lands
    /// on the stack rather than on a blade listing every operation the resource has.
    /// </summary>
    public string PortalUrl => PortalLink.ForLogs(ResourceId, PortalQuery, WindowMinutes);

    public bool HasPortalUrl => PortalUrl.Length > 0;

    /// <summary>What the finding is about: an operation name, or a dependency target.</summary>
    public string Subject { get; init; } = "";

    /// <summary>The measurement, e.g. "17% of 24 calls failed".</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// The number the rule tripped on, used to tell whether a muted finding has since worsened.
    /// </summary>
    public double Magnitude { get; init; }

    /// <summary>How much traffic this is about, for weighing what an ignore rule would hide.</summary>
    public long Calls { get; init; }

    /// <summary>The window the rule looked at, so the finding can reproduce itself.</summary>
    public int WindowMinutes { get; init; }

    /// <summary>The query that would show exactly what Huginn saw.</summary>
    public string EvidenceQuery => FindingQuery.Evidence(this);

    /// <summary>The query a person wants when they open this in the portal.</summary>
    public string PortalQuery => FindingQuery.Portal(this);

    /// <summary>
    /// Stable across polls so a finding can be muted or dismissed and still be recognised next
    /// time. Deliberately excludes the measurement, which moves every poll.
    /// </summary>
    public string Id => $"{AppId}|{Kind}|{Subject}";

    /// <summary>
    /// How loudly this kind of problem asks to be dealt with, highest first. Needed because
    /// <see cref="Magnitude"/> carries a different unit per kind, a percentage for a failure
    /// rate, milliseconds for latency and a call count for a dependency, so ordering on it alone
    /// ranks a slow route above one failing every call.
    /// </summary>
    public int Severity => Kind switch
    {
        // Not answering, or answering wrongly, beats answering slowly.
        FindingKind.FailureRate => 4,
        FindingKind.NoTraffic => 3,
        FindingKind.Dependency => 2,
        FindingKind.Latency => 1,
        _ => 0,
    };

    /// <summary>Sparkline of this measurement over the window, as block characters.</summary>
    public string Spark { get; set; } = "";

    /// <summary>
    /// The measurement behind <see cref="Spark"/>, oldest first. Kept because a reader that is
    /// not a person wants the numbers rather than the picture.
    /// </summary>
    public IReadOnlyList<double> SeriesValues { get; set; } = [];

    /// <summary>Whether it is new, as a phrase: "stepped up 35m ago", "steady all window".</summary>
    public string TrendVerdict { get; set; } = "";

    public bool HasTrend => Spark.Length > 0;

    /// <summary>Raised and not yet dismissed, so it stays at the top of its section.</summary>
    public bool IsFlagged { get; set; }

    public string FlagReason { get; set; } = "";

    public bool HasFlagReason => FlagReason.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteLabel))]
    [NotifyPropertyChangedFor(nameof(MuteIcon))]
    [NotifyPropertyChangedFor(nameof(MuteTooltip))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    private bool _isMuted;

    /// <summary>Magnitude when it was muted, so the card can show what it has grown from.</summary>
    public double MutedAtMagnitude { get; set; }

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    /// <summary>The label as a glyph. Card rows are narrow, and the tooltip carries the words.</summary>
    public string MuteIcon => IsMuted ? "🔊" : "🔇";

    public string MuteTooltip => IsMuted
        ? "Unmute, so this finding is listed and alerts normally again"
        : "Mute at its current level. It comes back if it gets substantially worse.";

    public double CardOpacity => IsMuted ? 0.55 : 1.0;

    public string KindIcon => Kind switch
    {
        FindingKind.FailureRate => "❌",
        FindingKind.Latency => "🐌",
        FindingKind.Dependency => "🔌",
        FindingKind.NoTraffic => "🕳",
        _ => "❓",
    };

    public string KindLabel => Kind switch
    {
        FindingKind.FailureRate => "failing",
        FindingKind.Latency => "slow",
        FindingKind.Dependency => "dependency",
        FindingKind.NoTraffic => "no traffic",
        _ => "",
    };
}
