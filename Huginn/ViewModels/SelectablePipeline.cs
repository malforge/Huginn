using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.ViewModels;

/// <summary>
/// Selectable wrapper around a pipeline definition for the settings picker.
/// </summary>
public partial class SelectablePipeline : ObservableObject
{
    public int Id { get; init; }
    public string DisplayName { get; init; } = "";

    [ObservableProperty] private bool _isSelected;
}
