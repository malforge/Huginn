using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Huginn.Models;

namespace Huginn.ViewModels;

/// <summary>
/// Selectable wrapper around an Application Insights resource for the settings picker.
/// </summary>
public partial class SelectableComponent : ObservableObject
{
    public string AppId { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>Full ARM id, kept so a portal link can be built later.</summary>
    public string ResourceId { get; init; } = "";

    /// <summary>Resource group and subscription, shown so near-identical names can be told apart.</summary>
    public string Qualifier { get; init; } = "";

    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Cloud roles to judge, comma separated, for a resource several services share. Empty judges
    /// the whole resource. Names match exactly, case included, as the monitor's filter does.
    /// </summary>
    [ObservableProperty] private string _roles = "";

    public List<string> RoleList =>
    [
        .. Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The roles reporting to the resource, plus any chosen that have not reported lately, so the
    /// names need not be looked up elsewhere. Ticking one edits <see cref="Roles"/>, and editing
    /// <see cref="Roles"/> moves the ticks.
    /// </summary>
    public ObservableCollection<SelectableRole> SeenRoles { get; } = [];

    /// <summary>What the role lookup found, or why it found nothing.</summary>
    [ObservableProperty] private string _rolesStatus = "";

    private bool _syncing;

    public void ShowSeenRoles(IReadOnlyList<CloudRole> seen)
    {
        foreach (SelectableRole old in SeenRoles) old.PropertyChanged -= OnRoleChanged;
        SeenRoles.Clear();

        HashSet<string> chosen = new(RoleList, StringComparer.Ordinal);
        foreach (CloudRole role in seen)
        {
            if (!SeenRoles.Any(r => r.Name == role.Name)) Add(new SelectableRole(role.Name, role, chosen.Contains(role.Name)));
        }

        _shown = true;
        AddUnseenChosen();
    }

    /// <summary>
    /// Whether the lookup has filled <see cref="SeenRoles"/>, after which a role typed by hand gets
    /// a row of its own, saying it was not seen when it matches nothing.
    /// </summary>
    private bool _shown;

    private void AddUnseenChosen()
    {
        foreach (string name in RoleList)
        {
            if (!SeenRoles.Any(r => r.Name == name)) Add(new SelectableRole(name, null, isSelected: true));
        }
    }

    private void Add(SelectableRole role)
    {
        role.PropertyChanged += OnRoleChanged;
        SeenRoles.Add(role);
    }

    private void OnRoleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(SelectableRole.IsSelected) || sender is not SelectableRole role) return;

        List<string> roles = RoleList;
        roles.Remove(role.Name);
        if (role.IsSelected) roles.Add(role.Name);

        _syncing = true;
        try { Roles = string.Join(", ", roles); }
        finally { _syncing = false; }
    }

    partial void OnRolesChanged(string value)
    {
        if (_syncing) return;

        HashSet<string> chosen = new(RoleList, StringComparer.Ordinal);
        _syncing = true;
        try
        {
            foreach (SelectableRole role in SeenRoles) role.IsSelected = chosen.Contains(role.Name);
            if (_shown) AddUnseenChosen();
        }
        finally { _syncing = false; }
    }
}
