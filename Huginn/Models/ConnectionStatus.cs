using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

/// <summary>Live health of one data source, surfaced in the settings list and the warning banner.</summary>
public sealed partial class ConnectionStatus : ObservableObject
{
    public ConnectionKind Kind { get; init; }

    /// <summary>Name shown to the user, e.g. "Azure DevOps".</summary>
    public string Title { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    private ConnectionState _state = ConnectionState.NotConfigured;

    /// <summary>Detail replacing the default state text, e.g. an error from the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string _message = "";

    /// <summary>The user must do something before this source can report.</summary>
    public bool NeedsAttention =>
        State is ConnectionState.NotConfigured or ConnectionState.AuthFailed or ConnectionState.Error;

    public bool IsConnected => State == ConnectionState.Connected;

    public string Glyph => State switch
    {
        ConnectionState.Connected => "✅",
        ConnectionState.Connecting => "⋯",
        ConnectionState.AuthFailed => "⚠️",
        ConnectionState.Error => "❌",
        _ => "○",
    };

    public string StateText => !string.IsNullOrEmpty(Message)
        ? Message
        : State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.AuthFailed => "Sign-in required",
            ConnectionState.Error => "Error",
            _ => "Not configured",
        };

    /// <summary>Moves the connection to a new state, clearing any stale detail.</summary>
    public void Set(ConnectionState state, string message = "")
    {
        State = state;
        Message = message;
    }
}
