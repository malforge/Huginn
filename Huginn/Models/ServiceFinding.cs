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

    /// <summary>What the finding is about: an operation name, or a dependency target.</summary>
    public string Subject { get; init; } = "";

    /// <summary>The measurement, e.g. "17% of 24 calls failed".</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// The number the rule tripped on, used to tell whether a muted finding has since worsened.
    /// </summary>
    public double Magnitude { get; init; }

    /// <summary>
    /// Stable across polls so a finding can be muted or dismissed and still be recognised next
    /// time. Deliberately excludes the measurement, which moves every poll.
    /// </summary>
    public string Id => $"{AppId}|{Kind}|{Subject}";

    /// <summary>Raised and not yet dismissed, so it stays at the top of its section.</summary>
    public bool IsFlagged { get; set; }

    public string FlagReason { get; set; } = "";

    public bool HasFlagReason => FlagReason.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteLabel))]
    [NotifyPropertyChangedFor(nameof(MuteTooltip))]
    [NotifyPropertyChangedFor(nameof(CardOpacity))]
    private bool _isMuted;

    /// <summary>Magnitude when it was muted, so the card can show what it has grown from.</summary>
    public double MutedAtMagnitude { get; set; }

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

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
