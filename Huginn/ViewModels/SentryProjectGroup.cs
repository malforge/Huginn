using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Huginn.Models;

namespace Huginn.ViewModels;

/// <summary>
/// One watched Sentry project and its issues. Each project is a separate app, so they get separate
/// sections rather than one merged list where an impact ordering would compare unlike things.
/// </summary>
public sealed partial class SentryProjectGroup : ObservableObject
{
    private List<SentryIssueItem> _all = [];

    public string Slug { get; init; } = "";

    public ObservableCollection<SentryIssueItem> Issues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMuted))]
    private int _mutedCount;

    public bool HasMuted => MutedCount > 0;

    /// <summary>Swaps this section over to its muted issues.</summary>
    [ObservableProperty] private bool _showMuted;

    [ObservableProperty] private bool _hasIssues;

    /// <summary>Replaces the issues for this project and republishes the visible ones.</summary>
    public void SetIssues(List<SentryIssueItem> issues)
    {
        _all = issues;
        Refresh();
    }

    private void Refresh()
    {
        MutedCount = _all.Count(i => i.IsMuted);

        // Unmuting the last one would leave this section showing an empty box with no obvious way
        // back, so it returns to the live view by itself.
        if (ShowMuted && MutedCount == 0)
        {
            ShowMuted = false;
            return;
        }

        Issues.Clear();
        foreach (SentryIssueItem issue in _all.Where(i => i.IsMuted == ShowMuted))
            Issues.Add(issue);

        HasIssues = _all.Count > 0;
    }

    partial void OnShowMutedChanged(bool value) => Refresh();
}
