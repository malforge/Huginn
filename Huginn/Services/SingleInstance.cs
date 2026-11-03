using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Huginn.Services;

/// <summary>
/// Keeps one window per profile. A second launch hands over to the copy already running, which
/// comes to the front, and exits.
/// </summary>
/// <remarks>
/// Two copies of one profile poll everything twice, notify twice, and only one of them can hold
/// the agents' port. The other runs on without it, unreachable to every agent.
/// <para>
/// The claim is a lock file in the profile's own folder, so it covers everything that shares that
/// folder: every launch by this person, from a terminal, Finder or sign-in alike. A named mutex
/// does not, because on macOS .NET scopes it to the Unix session, and a terminal is a session of
/// its own. The operating system lets go of the file when the process ends, crash included.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Raised on a background thread when another launch asks this copy to show itself.</summary>
    public event Action? ActivationRequested;

    private SingleInstance(FileStream claim, string pipeName)
    {
        _lock = claim;
        _pipeName = pipeName;
        _ = Task.Run(ListenAsync);
    }

    /// <summary>
    /// Claims the profile whose folder is <paramref name="directory"/>, or returns null when it
    /// cannot: another copy holds it, or something else has the file open. <paramref name="name"/>
    /// names the channel a later launch signals on.
    /// </summary>
    /// <remarks>
    /// Tried a few times before giving up, since a virus scanner or indexer opening the file holds
    /// it for a moment. Whether a copy is actually running is for <see cref="SignalRunning"/> to
    /// say: null alone does not mean one is.
    /// </remarks>
    public static SingleInstance? TryAcquire(string directory, string name)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(directory);
                FileStream claim = new(
                    Path.Combine(directory, "instance.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new SingleInstance(claim, PipeName(name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ClaimAttempts) return null;
                Thread.Sleep(ClaimRetryDelay);
            }
        }
    }

    private const int ClaimAttempts = 4;
    private static readonly TimeSpan ClaimRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Asks the copy holding <paramref name="name"/> to come to the front.</summary>
    public static bool SignalRunning(string name, TimeSpan timeout)
    {
        // Windows lets only the process the person just launched take the foreground. Passing
        // that right on is what lets the running copy's window come forward rather than flash.
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AsfwAny);

        try
        {
            using NamedPipeClientStream client = new(
                ".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Scoped like the claim, to this person. Pipe names are shared by the whole machine, so
    /// another person signed in at the same time would otherwise hold it already.
    /// </summary>
    private static string PipeName(string name) => $"{name}.{Environment.UserName}.activate";

    /// <summary>Each connection is one request to come forward; nothing is read from it.</summary>
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream server = new(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Single instance: could not listen for another launch: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lock.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
