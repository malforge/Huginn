using System;
using System.Collections.Generic;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

[Collection(SettingsFile.Name)]
public sealed class AppInsightsRolesAndSilenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly AppInsightsComponent Shared = new()
    {
        Name = "prod-insights",
        AppId = "app",
        ResourceId = "/subscriptions/s/resourceGroups/g/providers/microsoft.insights/components/c",
        Roles = ["mobile-server"],
    };

    private static readonly AppInsightsComponent Whole = new() { Name = "prod-insights", AppId = "app" };

    [Fact]
    public void A_busy_resource_that_has_gone_quiet_is_reported_silent_before_the_window_runs_out()
    {
        ServiceFinding? finding = AppInsightsMonitor.NoTrafficFinding(
            Whole, 10080, total: 321_667, lastHour: 0, lastSeen: "2026-10-23T01:26:29Z", Now);

        Assert.NotNull(finding);
        Assert.Equal(FindingKind.NoTraffic, finding.Kind);
        Assert.Equal("no requests for 6d, after about 1,915 an hour", finding.Detail);
    }

    [Fact]
    public void A_resource_that_is_always_quiet_is_not_called_silent_for_a_quiet_hour()
    {
        Assert.Null(AppInsightsMonitor.NoTrafficFinding(
            Whole, 10080, total: 50, lastHour: 0, lastSeen: "2026-10-29T09:00:00Z", Now));
    }

    [Fact]
    public void A_resource_still_answering_is_not_silent()
    {
        Assert.Null(AppInsightsMonitor.NoTrafficFinding(
            Whole, 10080, total: 321_667, lastHour: 1_800, lastSeen: "2026-10-29T11:59:00Z", Now));
    }

    [Fact]
    public void A_resource_with_nothing_in_the_whole_window_is_silent()
    {
        ServiceFinding? finding = AppInsightsMonitor.NoTrafficFinding(
            Whole, 720, total: 0, lastHour: 0, lastSeen: "", Now);

        Assert.NotNull(finding);
        Assert.Equal("no requests in the last 12h", finding.Detail);
    }

    [Fact]
    public void A_resource_watched_for_some_roles_only_queries_those_roles()
    {
        Assert.Equal("| where cloud_RoleName in (\"mobile-server\")", Shared.RoleClause);
        Assert.Equal("", Whole.RoleClause);
    }

    [Fact]
    public void A_finding_on_a_role_says_which_and_opens_on_that_role_alone()
    {
        QueryTable routes = new(
            ["name", "total", "recorded", "failed", "notFound", "p95", "codes", "users", "lastFailed"],
            [["GET /api/orders/load", "420", "15", "420", "0", "120", "500", "6", "2026-10-29T11:00:00Z"]]);

        ServiceFinding finding = Assert.Single(AppInsightsMonitor.FromRoutes(
            routes, Shared, 720, new AppSettings(new NoCredentials()), [], Now));

        Assert.Equal("prod-insights › mobile-server", finding.ResourceName);
        Assert.Contains("| where cloud_RoleName in (\"mobile-server\")", FindingQuery.Portal(finding));
        Assert.Contains("| where cloud_RoleName in (\"mobile-server\")", FindingQuery.Evidence(finding));
    }

    [Theory]
    [InlineData(FindingKind.FailureRate)]
    [InlineData(FindingKind.Latency)]
    [InlineData(FindingKind.Dependency)]
    [InlineData(FindingKind.Exception)]
    [InlineData(FindingKind.NoTraffic)]
    public void Every_query_behind_a_finding_closes_its_line_with_the_role_filter(FindingKind kind)
    {
        ServiceFinding finding = new() { Kind = kind, Subject = "s", WindowMinutes = 720, RoleClause = Shared.RoleClause };

        List<string> queries = [FindingQuery.Evidence(finding), FindingQuery.Portal(finding)];
        foreach (Drilldown drilldown in FindingQuery.Explanations(finding)) queries.Add(drilldown.Kql);

        foreach (string query in queries)
        {
            Assert.Contains(Shared.RoleClause, query);
            foreach (string line in query.Split('\n'))
                if (line.Contains("cloud_RoleName in")) Assert.EndsWith(Shared.RoleClause, line.TrimEnd());
        }
    }

    [Fact]
    public void The_roles_chosen_for_a_resource_survive_a_restart()
    {
        AppSettings settings = new(new NoCredentials());
        settings.AppInsightsRoles["app"] = ["mobile-server"];
        settings.Save();

        AppSettings loaded = AppSettings.Load(new NoCredentials());

        Assert.Equal(["mobile-server"], loaded.AppInsightsRoles["app"]);
    }
}
