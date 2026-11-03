namespace Huginn.Models;

/// <summary>A service reporting to an Application Insights resource, and how much it sent.</summary>
/// <param name="Name">Its cloud_RoleName.</param>
/// <param name="Requests">Requests it served over <see cref="CloudRoles.LookBack"/>, scaled up from sampling.</param>
/// <param name="Other">Dependency calls and exceptions it recorded over the same time, likewise scaled.</param>
public sealed record CloudRole(string Name, long Requests, long Other);
