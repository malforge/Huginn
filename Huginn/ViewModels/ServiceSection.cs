using System.Collections.ObjectModel;
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
public sealed class ServiceSection
{
    public FindingKind Kind { get; init; }

    public string Heading { get; init; } = "";

    public ObservableCollection<ServiceFinding> Findings { get; } = [];
}
