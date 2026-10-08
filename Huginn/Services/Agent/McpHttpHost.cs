using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Huginn.Services.Agent;

/// <summary>
/// Serves MCP over HTTP from the running app. Claude Code reconnects to an HTTP server that comes
/// back on the same address, which it does not do for a stdio server, so an agent's connection
/// outlives the restart an update brings.
/// </summary>
/// <remarks>
/// Answers every request with a single JSON reply and offers no event stream, which the protocol
/// allows and which is all a server that never speaks first needs. It listens on this machine
/// only, and answers only a caller holding the token handed over at registration, since any
/// program on the machine, another user's included, can reach a local port.
/// </remarks>
public sealed class McpHttpHost : IDisposable
{
    /// <summary>Far above any real request, so a runaway client cannot make the app buffer without limit.</summary>
    private const int MaxBodyBytes = 1 << 20;

    private readonly HttpListener _listener = new();
    private readonly byte[] _expectedAuthorization;
    private readonly Func<string, string?> _respond;

    public McpHttpHost(int port, string token, Func<string, string?> respond)
    {
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _expectedAuthorization = Encoding.UTF8.GetBytes($"Bearer {token}");
        _respond = respond;
    }

    /// <summary>Starts listening. Throws when the port cannot be had, typically because it is taken.</summary>
    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"MCP over HTTP: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            if (request.Url?.AbsolutePath != McpEndpoint.Path)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // A web page the user has open can send requests to a local port. Refusing a
            // foreign origin keeps such a page from reaching the server even with a token.
            if (!IsLocalOrigin(request.Headers["Origin"]))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            if (!IsAuthorized(request.Headers["Authorization"]))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                response.AddHeader("Allow", "POST");
                return;
            }

            if (await ReadBodyAsync(request) is not { } body)
            {
                response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                return;
            }

            if (_respond(body) is not { } answer)
            {
                response.StatusCode = (int)HttpStatusCode.Accepted;
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(answer);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            Log.Error($"MCP over HTTP: {ex.Message}");
            try { response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private bool IsAuthorized(string? header) =>
        header != null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), _expectedAuthorization);

    /// <summary>No origin at all is a program rather than a browser, and is let through to the token check.</summary>
    private static bool IsLocalOrigin(string? origin) =>
        string.IsNullOrEmpty(origin)
        || (Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && uri.IsLoopback);

    /// <summary>The body as text, or null when it is larger than any real request.</summary>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes) return null;

        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes) return null;
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose() => _listener.Close();
}
