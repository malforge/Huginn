using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Huginn.Models;

namespace Huginn.ViewModels;

/// <summary>
/// One kind of service finding under a heading that says what it is and how it is ranked, so
/// the order of a list never has to be guessed.
/// </summary>
/// <remarks>
/// Every section is always shown, empty or not, so a kind with nothing wrong reads as checked
/// rather than as missing, and nothing below it moves when a finding comes or goes.
/// </remarks>
public sealed partial class ServiceSection : ObservableObject
{
    public FindingKind Kind { get; init; }

    public string Heading { get; init; } = "";

    public ObservableCollection<ServiceFinding> Findings { get; } = [];

    /// <summary>
    /// Of this kind, how many were held back for having too little recorded to judge. Counted on
    /// the pane rather than only in settings, so what the rule hides is in sight of what it shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooFewLine))]
    [NotifyPropertyChangedFor(nameof(HasTooFew))]
    private int _tooFewCount;

    public bool HasTooFew => TooFewCount > 0;

    /// <summary>Always shown, so the section ends in the same place whatever the count.</summary>
    public string TooFewLine => TooFewCount == 0
        ? "Too few recorded to judge: none"
        : $"Too few recorded to judge: {TooFewCount:N0} more ›";
}
