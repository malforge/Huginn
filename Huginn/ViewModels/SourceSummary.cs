using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.ViewModels;

/// <summary>
/// A single line standing in for everything not currently demanding attention in one project or
/// resource. Fixed height whatever the volume, which is the point: Huginn says what exists and
/// how healthy it looks, and the source's own tools are where you go to read it all.
/// </summary>
public sealed partial class SourceSummary : ObservableObject
{
    public string Name { get; init; } = "";

    /// <summary>Where to go to see the whole thing. Empty when there is no useful link.</summary>
    public string Url { get; init; } = "";

    public bool HasUrl => Url.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    private int _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    [NotifyPropertyChangedFor(nameof(HasFlagged))]
    private int _flagged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    private int _muted;

    /// <summary>
    /// What kind of trouble it is, e.g. "19 slow · 4 failing". A bare count says how much is
    /// wrong without saying what, which is the one thing the row is there to answer.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    private string _breakdown = "";

    /// <summary>Something here is raised, so the row is worth looking at rather than skimming.</summary>
    public bool HasFlagged => Flagged > 0;

    public string Line
    {
        get
        {
            if (Total == 0) return "nothing outstanding";

            string line = Breakdown.Length > 0 ? Breakdown : $"{Total} outstanding";
            if (Flagged > 0) line += $" · {Flagged} raised";
            if (Muted > 0) line += $" · {Muted} muted";
            return line;
        }
    }
}
