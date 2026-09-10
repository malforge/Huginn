using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.ViewModels;

/// <summary>
/// Selectable wrapper around an Application Insights resource for the settings picker.
/// </summary>
public partial class SelectableComponent : ObservableObject
{
    public string AppId { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>Resource group and subscription, shown so near-identical names can be told apart.</summary>
    public string Qualifier { get; init; } = "";

    [ObservableProperty] private bool _isSelected;
}
