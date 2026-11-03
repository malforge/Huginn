using System;
using System.Diagnostics;
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
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Raised on a background thread when another launch asks this copy to show itself.</summary>
    public event Action? ActivationRequested;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _ = Task.Run(ListenAsync);
    }

    /// <summary>
    /// Claims <paramref name="name"/> for this process, or returns null when another copy holds it.
    /// Dispose on the thread that claimed it, since the claim belongs to that thread.
    /// </summary>
    public static SingleInstance? TryAcquire(string name)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, MutexName(name));
        }
        catch (UnauthorizedAccessException)
        {
            // Held by a copy running elevated, which this one may not even open. It still runs.
            return null;
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The holder ended without letting go, a crash typically. The claim is ours now.
            owned = true;
        }

        if (owned) return new SingleInstance(mutex, PipeName(name));

        mutex.Dispose();
        return null;
    }

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
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string MutexName(string name) => $"{name}.instance";

    /// <summary>
    /// Scoped like the claim, to this person's session. Pipe names are shared by the whole
    /// machine, so another person signed in at the same time would otherwise hold it already.
    /// </summary>
    private static string PipeName(string name) =>
        $"{name}.{Environment.UserName}.{SessionId()}.activate";

    private static int SessionId()
    {
        using Process self = Process.GetCurrentProcess();
        return self.SessionId;
    }

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
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
