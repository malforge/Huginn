using System;
using System.Threading;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => $"Huginn-tests-{Guid.NewGuid():N}";

    /// <summary>
    /// The running copy, on a thread of its own. A claim belongs to the thread that made it, and a
    /// second claim from that same thread would be granted.
    /// </summary>
    private sealed class RunningCopy : IDisposable
    {
        private readonly ManualResetEventSlim _claimed = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public SingleInstance? Instance { get; private set; }

        public RunningCopy(string name)
        {
            _thread = new Thread(() =>
            {
                using SingleInstance? instance = SingleInstance.TryAcquire(name);
                Instance = instance;
                _claimed.Set();
                _release.Wait();
            });
            _thread.Start();
            _claimed.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
        }
    }

    [Fact]
    public void A_second_launch_is_turned_away_while_the_first_runs()
    {
        string name = UniqueName();
        using RunningCopy first = new(name);

        Assert.NotNull(first.Instance);
        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void A_second_launch_brings_the_running_copy_forward()
    {
        string name = UniqueName();
        using RunningCopy first = new(name);
        using ManualResetEventSlim activated = new();
        first.Instance!.ActivationRequested += activated.Set;

        Assert.True(SingleInstance.SignalRunning(name, TimeSpan.FromSeconds(5)));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_launch_after_the_first_copy_exits_runs()
    {
        string name = UniqueName();
        new RunningCopy(name).Dispose();

        using SingleInstance? next = SingleInstance.TryAcquire(name);
        Assert.NotNull(next);
    }

    [Fact]
    public void Profiles_do_not_turn_each_other_away()
    {
        using RunningCopy installed = new(UniqueName());

        using SingleInstance? other = SingleInstance.TryAcquire(UniqueName());
        Assert.NotNull(other);
    }

    [Fact]
    public void Signalling_with_nothing_running_reports_it()
    {
        Assert.False(SingleInstance.SignalRunning(UniqueName(), TimeSpan.FromMilliseconds(200)));
    }
}
