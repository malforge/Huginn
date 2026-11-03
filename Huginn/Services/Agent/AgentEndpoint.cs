using System;
using System.Threading;

namespace Huginn.Services.Agent;

/// <summary>
/// Keeps <see cref="McpHttpHost"/> listening for as long as the app runs. A port that cannot be
/// had is tried again until it can, so a copy that started while another still held the port
/// takes it over once that copy is gone.
/// </summary>
public sealed class AgentEndpoint : IDisposable
{
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(10);

    private readonly int _port;
    private readonly string _token;
    private readonly Func<string, string?> _respond;
    private readonly TimeSpan _retryInterval;
    private readonly object _lock = new();

    private McpHttpHost? _host;
    private Timer? _retry;
    private bool _disposed;

    public AgentEndpoint(int port, string token, Func<string, string?> respond, TimeSpan retryInterval)
    {
        _port = port;
        _token = token;
        _respond = respond;
        _retryInterval = retryInterval;
    }

    public string Url => $"http://localhost:{_port}{McpEndpoint.Path}";

    /// <summary>Why agents cannot reach this copy right now, or null when they can.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised on a background thread when the endpoint starts listening after a retry.</summary>
    public event Action? Changed;

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _host != null || _retry != null) return;
            if (TryListen()) return;
            _retry = new Timer(_ => Retry(), null, _retryInterval, _retryInterval);
        }
    }

    private void Retry()
    {
        lock (_lock)
        {
            if (_disposed || _host != null || !TryListen()) return;
            _retry?.Dispose();
            _retry = null;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A fresh listener each time, since one whose start failed is not reliably reusable. The
    /// failure is logged once rather than on every retry.
    /// </summary>
    private bool TryListen()
    {
        McpHttpHost host = new(_port, _token, _respond);
        try
        {
            host.Start();
        }
        catch (Exception ex)
        {
            host.Dispose();
            bool first = Error == null;
            Error = $"Could not open {Url}: {ex.Message} Trying again every {_retryInterval.TotalSeconds:0} seconds.";
            if (first) Log.Error(Error);
            return false;
        }

        _host = host;
        Error = null;
        Log.Info($"MCP over HTTP at {Url}");
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _retry?.Dispose();
            _retry = null;
            _host?.Dispose();
            _host = null;
        }
    }
}
