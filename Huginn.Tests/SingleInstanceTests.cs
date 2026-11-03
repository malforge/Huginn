using System;
using System.IO;
using System.Threading;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"Huginn-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A profile of its own: its folder and its signalling name.</summary>
    private (string Directory, string Name) Profile()
    {
        string name = $"Huginn-tests-{Guid.NewGuid():N}";
        return (Path.Combine(_root, name), name);
    }

    [Fact]
    public void A_second_launch_is_turned_away_while_the_first_runs()
    {
        var (dir, name) = Profile();
        using SingleInstance? first = SingleInstance.TryAcquire(dir, name);

        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(dir, name));
    }

    [Fact]
    public void A_second_launch_brings_the_running_copy_forward()
    {
        var (dir, name) = Profile();
        using SingleInstance? first = SingleInstance.TryAcquire(dir, name);
        using ManualResetEventSlim activated = new();
        first!.ActivationRequested += activated.Set;

        Assert.True(SingleInstance.SignalRunning(name, TimeSpan.FromSeconds(5)));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_launch_after_the_first_copy_exits_runs()
    {
        var (dir, name) = Profile();
        SingleInstance.TryAcquire(dir, name)!.Dispose();

        using SingleInstance? next = SingleInstance.TryAcquire(dir, name);
        Assert.NotNull(next);
    }

    [Fact]
    public void Profiles_do_not_turn_each_other_away()
    {
        var (dir, name) = Profile();
        var (otherDir, otherName) = Profile();
        using SingleInstance? installed = SingleInstance.TryAcquire(dir, name);

        using SingleInstance? other = SingleInstance.TryAcquire(otherDir, otherName);
        Assert.NotNull(installed);
        Assert.NotNull(other);
    }

    [Fact]
    public void Another_program_holding_the_file_for_a_moment_does_not_turn_a_launch_away()
    {
        var (dir, name) = Profile();
        Directory.CreateDirectory(dir);
        FileStream scanner = new(
            Path.Combine(dir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        using Timer done = new(_ => scanner.Dispose(), null, 100, Timeout.Infinite);

        using SingleInstance? instance = SingleInstance.TryAcquire(dir, name);

        Assert.NotNull(instance);
    }

    [Fact]
    public void Signalling_with_nothing_running_reports_it()
    {
        Assert.False(SingleInstance.SignalRunning(Profile().Name, TimeSpan.FromMilliseconds(200)));
    }
}
