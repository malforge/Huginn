using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Huginn.Models;

namespace Huginn.ViewModels;

/// <summary>
/// One watched Application Insights resource and the findings on it. Resources are separate
/// services, so they get separate sections rather than one merged list.
/// </summary>
public sealed partial class ServiceFindingGroup : ObservableObject
{
    private List<ServiceFinding> _all = [];

    public string ResourceName { get; init; } = "";

    public ObservableCollection<ServiceFinding> Findings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMuted))]
    private int _mutedCount;

    public bool HasMuted => MutedCount > 0;

    /// <summary>Swaps this section over to its muted findings.</summary>
    [ObservableProperty] private bool _showMuted;

    [ObservableProperty] private bool _hasFindings;

    public void SetFindings(List<ServiceFinding> findings)
    {
        _all = findings;
        Refresh();
    }

    private void Refresh()
    {
        MutedCount = _all.Count(f => f.IsMuted);

        // Unmuting the last one would leave an empty box with no obvious way back.
        if (ShowMuted && MutedCount == 0)
        {
            ShowMuted = false;
            return;
        }

        Findings.Clear();
        foreach (ServiceFinding finding in _all.Where(f => f.IsMuted == ShowMuted))
            Findings.Add(finding);

        HasFindings = _all.Count > 0;
    }

    partial void OnShowMutedChanged(bool value) => Refresh();
}
