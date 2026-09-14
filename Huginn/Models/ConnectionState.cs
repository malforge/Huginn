namespace Huginn.Models;

/// <summary>Health of a single connection, as shown in the settings header and the banner.</summary>
public enum ConnectionState
{
    NotConfigured,
    Connecting,
    Connected,
    AuthFailed,
    Error,
}
