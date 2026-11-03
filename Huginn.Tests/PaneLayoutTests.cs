using System.Linq;
using Huginn.Models;
using Huginn.Views;
using Xunit;

namespace Huginn.Tests;

public sealed class PaneLayoutTests
{
    private static readonly PaneKind[] All = [PaneKind.AzureDevOps, PaneKind.Sentry, PaneKind.Services];

    [Fact]
    public void Without_focus_every_pane_gets_an_even_share_side_by_side_when_they_fit()
    {
        var (stacked, tracks) = PaneLayout.Arrange(All, PaneKind.None, 1300);

        Assert.False(stacked);
        Assert.All(tracks, t => Assert.False(t.Collapsed));
    }

    [Fact]
    public void Without_focus_panes_that_do_not_all_fit_are_stacked_rather_than_orphaned()
    {
        var (stacked, _) = PaneLayout.Arrange(All, PaneKind.None, 900);

        Assert.True(stacked);
    }

    [Fact]
    public void A_focused_pane_leaves_the_others_as_slivers()
    {
        var (_, tracks) = PaneLayout.Arrange(All, PaneKind.Sentry, 1300);

        Assert.Equal([PaneKind.AzureDevOps, PaneKind.Services], tracks.Where(t => t.Collapsed).Select(t => t.Pane));
        Assert.False(tracks.Single(t => t.Pane == PaneKind.Sentry).Collapsed);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(900)]
    [InlineData(1300)]
    public void Focus_keeps_the_arrangement_the_window_already_has(double width)
    {
        var (evenly, _) = PaneLayout.Arrange(All, PaneKind.None, width);
        var (focused, tracks) = PaneLayout.Arrange(All, PaneKind.Sentry, width);

        Assert.Equal(evenly, focused);
        Assert.Equal(2, tracks.Count(t => t.Collapsed));
    }

    [Fact]
    public void Focus_on_a_pane_that_is_not_shown_is_ignored()
    {
        PaneKind[] shown = [PaneKind.AzureDevOps, PaneKind.Sentry];

        var (_, tracks) = PaneLayout.Arrange(shown, PaneKind.Services, 1300);

        Assert.All(tracks, t => Assert.False(t.Collapsed));
    }

    [Fact]
    public void A_lone_pane_is_never_collapsed()
    {
        var (_, tracks) = PaneLayout.Arrange([PaneKind.Sentry], PaneKind.Sentry, 1300);

        Assert.False(Assert.Single(tracks).Collapsed);
    }
}
