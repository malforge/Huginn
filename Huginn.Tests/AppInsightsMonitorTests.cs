using System;
using System.Collections.Generic;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class AppInsightsMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly AppInsightsComponent Component = new()
    {
        Name = "prod-insights",
        AppId = "app",
        ResourceId = "/subscriptions/s/resourceGroups/g/providers/microsoft.insights/components/c",
    };

    private static QueryTable Routes(params string[][] rows) =>
        new(["name", "total", "recorded", "failed", "notFound", "p95", "codes", "users", "lastFailed"], rows);

    private static QueryTable Dependencies(params string[][] rows) =>
        new(["type", "target", "calls", "recorded", "failed", "failedRecorded", "codes", "users", "lastFailed"], rows);

    private static List<ServiceFinding> FromRoutes(QueryTable routes) =>
        AppInsightsMonitor.FromRoutes(routes, Component, 720, new AppSettings(new NoCredentials()), [], Now);

    [Fact]
    public void A_failing_route_says_how_much_was_recorded_who_it_hit_and_when_it_last_failed()
    {
        ServiceFinding finding = Assert.Single(FromRoutes(Routes(
            ["GET /api/orders/load", "420", "15", "420", "0", "120", "403", "6", "2026-10-20T09:00:00Z"])));

        Assert.Equal(FindingKind.FailureRate, finding.Kind);
        Assert.Equal("100% of ~420 calls failed, code 403", finding.Detail);
        Assert.Equal("15 recorded · at least 6 users · last failed 3h ago", finding.Context);
    }

    [Fact]
    public void A_route_seen_in_too_few_recorded_requests_is_not_judged_however_many_they_stand_for()
    {
        Assert.Empty(FromRoutes(Routes(
            ["GET /api/teams/range", "30", "1", "30", "0", "120", "498", "1", "2026-10-20T09:00:00Z"])));
    }

    [Fact]
    public void An_unsampled_route_gives_its_counts_as_exact()
    {
        ServiceFinding finding = Assert.Single(FromRoutes(Routes(
            ["GET Invoice/Preview", "56", "56", "56", "0", "120", "500", "1", "2026-10-20T11:00:00Z"])));

        Assert.Equal("100% of 56 calls failed, code 500", finding.Detail);
        Assert.Equal("1 user · last failed 1h ago", finding.Context);
    }

    [Fact]
    public void A_slow_route_reads_in_US_English()
    {
        ServiceFinding finding = Assert.Single(FromRoutes(Routes(
            ["POST Approval/ApproveList", "1692", "60", "0", "0", "13974", "", "0", ""])));

        Assert.Equal("p95 14.0s over ~1,692 calls", finding.Detail);
        Assert.Equal("60 recorded", finding.Context);
    }

    [Fact]
    public void A_failing_dependency_is_one_finding_with_its_share_of_calls_and_every_code()
    {
        List<ServiceFinding> findings = AppInsightsMonitor.FromDependencies(Dependencies(
            ["Azure blob", "store.blob.core.windows.net", "1203", "240", "65", "12", "409, 412", "3", "2026-10-20T11:58:00Z"]),
            Component, 720, Now);

        ServiceFinding finding = Assert.Single(findings);
        Assert.Equal("Azure blob store.blob.core.windows.net", finding.Subject);
        Assert.Equal("~65 of ~1,203 calls failed (5%), codes 409, 412", finding.Detail);
        Assert.Equal("12 failures recorded · at least 3 users · last failed 2m ago", finding.Context);
        Assert.Equal(65, finding.Magnitude);
    }

    [Fact]
    public void A_dependency_with_too_few_recorded_failures_is_not_raised()
    {
        Assert.Empty(AppInsightsMonitor.FromDependencies(Dependencies(
            ["HTTP", "registry.example.org", "300", "10", "58", "2", "400", "1", "2026-10-20T11:58:00Z"]),
            Component, 720, Now));
    }
}
