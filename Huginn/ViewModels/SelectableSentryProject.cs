using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.ViewModels;

/// <summary>
/// Selectable wrapper around a Sentry project for the settings picker.
/// </summary>
public partial class SelectableSentryProject : ObservableObject
{
    /// <summary>The project slug, which is also how issues identify their project.</summary>
    public string Slug { get; init; } = "";

    [ObservableProperty] private bool _isSelected;
}
