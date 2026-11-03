using System.Collections.Generic;
using System.Linq;
using Huginn.Models;

namespace Huginn.Views;

/// <summary>Where the panes go, given which are shown, which has focus and the room there is.</summary>
public static class PaneLayout
{
    /// <summary>Width each pane needs before another column is worth having.</summary>
    public const double PaneWidth = 420;

    /// <summary>What is left of a pane while another has focus: room for its name and a count.</summary>
    public const double Sliver = 36;

    /// <summary>One pane's place in the arrangement, in order.</summary>
    public sealed record Track(PaneKind Pane, bool Collapsed);

    /// <summary>
    /// Side by side when every pane fits, else stacked; anything between, such as two columns for
    /// three panes, would leave one orphaned on a row of its own, which looks like a mistake. A
    /// pane with focus takes all the room but a sliver for each of the others, in whichever of the
    /// two arrangements the window has: focus never turns rows into columns, so the button just
    /// clicked stays where it was.
    /// </summary>
    public static (bool Stacked, IReadOnlyList<Track> Tracks) Arrange(
        IReadOnlyList<PaneKind> shown, PaneKind focused, double width)
    {
        bool focusing = focused != PaneKind.None && shown.Count > 1 && shown.Contains(focused);

        bool stacked = shown.Count < 2 || width < PaneWidth * shown.Count;

        return (stacked, [.. shown.Select(p => new Track(p, focusing && p != focused))]);
    }
}
