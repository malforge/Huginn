using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Huginn.Models;

namespace Huginn.ViewModels;

/// <summary>One cloud role in a shared resource, ticked when only the ticked roles are judged.</summary>
public partial class SelectableRole : ObservableObject
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public SelectableRole(string name, CloudRole? seen, bool isSelected)
    {
        Name = name;
        Seen = seen;
        _isSelected = isSelected;
    }

    public string Name { get; }

    /// <summary>What the role sent over the last day, or none when it was chosen but not seen.</summary>
    public CloudRole? Seen { get; }

    public string Detail => Seen switch
    {
        null => "nothing recorded in the last day",
        { Requests: > 0 } s => $"~{Plural(s.Requests, "request")} in the last day",
        var s => $"no requests, ~{Plural(s.Other, "dependency call or exception", "dependency calls and exceptions")} in the last day",
    };

    private static string Plural(long n, string one, string? many = null) =>
        $"{n.ToString("N0", English)} {(n == 1 ? one : many ?? one + "s")}";

    [ObservableProperty] private bool _isSelected;
}
