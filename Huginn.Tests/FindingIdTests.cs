using System;
using System.IO;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

[Collection(SettingsFile.Name)]
public sealed class FindingIdTests
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Huginn-tests", "settings.json");

    private static ServiceFinding Route(FindingKind kind, string subject) =>
        new() { AppId = "app", Kind = kind, Subject = subject };

    [Theory]
    [InlineData(FindingKind.FailureRate)]
    [InlineData(FindingKind.Latency)]
    public void A_route_keeps_its_id_whichever_of_its_spellings_is_shown(FindingKind kind)
    {
        Assert.Equal(Route(kind, "GET /api/Orders").Id, Route(kind, "GET /api/orders/").Id);
    }

    [Fact]
    public void Dependencies_differing_in_case_are_different_findings()
    {
        Assert.NotEqual(Route(FindingKind.Dependency, "HTTP Foo.com").Id,
                        Route(FindingKind.Dependency, "HTTP foo.com").Id);
    }

    [Fact]
    public void A_route_muted_before_routes_were_grouped_is_still_muted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            { "MutedFindings": { "app|FailureRate|POST /v2/Customers": 76.5,
                                 "app|Dependency|HTTP Foo.com": 12 } }
            """);

        AppSettings loaded = AppSettings.Load(new NoCredentials());

        Assert.True(loaded.IsFindingMuted(Route(FindingKind.FailureRate, "POST /v2/customers").Id));
        Assert.Equal(76.5, loaded.GetFindingMutedAt(Route(FindingKind.FailureRate, "POST /v2/Customers").Id));
        Assert.True(loaded.IsFindingMuted(Route(FindingKind.Dependency, "HTTP Foo.com").Id));
    }
}
