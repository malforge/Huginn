using System;
using System.IO;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

[Collection(SettingsFile.Name)]
public sealed class NotificationSettingsTests
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Huginn-tests", "settings.json");

    [Fact]
    public void Turning_a_source_off_survives_a_restart()
    {
        AppSettings settings = new(new NoCredentials())
        {
            NotifyPullRequests = false,
            NotifyBuilds = true,
            NotifyCrashes = false,
            NotifyServices = false,
        };
        settings.Save();

        AppSettings loaded = AppSettings.Load(new NoCredentials());

        Assert.False(loaded.NotifyPullRequests);
        Assert.True(loaded.NotifyBuilds);
        Assert.False(loaded.NotifyCrashes);
        Assert.False(loaded.NotifyServices);
    }

    [Fact]
    public void Settings_saved_before_the_switches_existed_keep_every_notification_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "Organization": "org" }""");

        AppSettings loaded = AppSettings.Load(new NoCredentials());

        Assert.True(loaded.NotifyPullRequests);
        Assert.True(loaded.NotifyBuilds);
        Assert.True(loaded.NotifyCrashes);
        Assert.True(loaded.NotifyServices);
    }
}
