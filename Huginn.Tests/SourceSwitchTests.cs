using System;
using System.IO;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

[Collection(SettingsFile.Name)]
public sealed class SourceSwitchTests
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Huginn-tests", "settings.json");

    [Fact]
    public void A_source_turned_off_stays_off_after_a_restart()
    {
        AppSettings settings = new(new NoCredentials())
        {
            AdoEnabled = true,
            SentryEnabled = false,
            AppInsightsEnabled = false,
        };
        settings.Save();

        AppSettings loaded = AppSettings.Load(new NoCredentials());

        Assert.True(loaded.AdoEnabled);
        Assert.False(loaded.SentryEnabled);
        Assert.False(loaded.AppInsightsEnabled);
    }

    [Fact]
    public void Settings_saved_before_the_switches_existed_keep_every_source_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "Organization": "org" }""");

        AppSettings loaded = AppSettings.Load(new NoCredentials());

        Assert.True(loaded.AdoEnabled);
        Assert.True(loaded.SentryEnabled);
        Assert.True(loaded.AppInsightsEnabled);
    }

    [Theory]
    [InlineData(ConnectionState.NotConfigured)]
    [InlineData(ConnectionState.AuthFailed)]
    [InlineData(ConnectionState.Error)]
    public void A_source_turned_off_asks_for_nothing_whatever_it_was_left_in(ConnectionState state)
    {
        ConnectionStatus status = new() { Title = "Application Insights" };
        status.Set(state, "Sign-in expired, sign in again");
        Assert.True(status.NeedsAttention);

        status.IsEnabled = false;

        Assert.False(status.NeedsAttention);
        Assert.Equal("Turned off", status.StateText);
    }

    [Fact]
    public void A_source_turned_back_on_reports_its_state_again()
    {
        ConnectionStatus status = new() { Title = "Sentry", IsEnabled = false };
        status.Set(ConnectionState.AuthFailed, "Check the token");

        status.IsEnabled = true;

        Assert.True(status.NeedsAttention);
        Assert.Equal("Check the token", status.StateText);
    }
}
