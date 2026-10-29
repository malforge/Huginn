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

    private static List<ServiceFinding> FromDependencies(QueryTable dependencies, List<IgnoredSubject>? heldBack = null) =>
        AppInsightsMonitor.FromDependencies(dependencies, Component, 720, new AppSettings(new NoCredentials()), heldBack ?? [], Now);

    private static List<ServiceFinding> FromExceptions(QueryTable exceptions, List<IgnoredSubject>? heldBack = null) =>
        AppInsightsMonitor.FromExceptions(exceptions, Component, 720, new AppSettings(new NoCredentials()), heldBack ?? [], Now);

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
    public void A_route_failing_in_too_few_recorded_requests_is_held_back_and_says_why()
    {
        List<IgnoredSubject> heldBack = [];

        List<ServiceFinding> findings = AppInsightsMonitor.FromRoutes(Routes(
            ["GET /api/teams/range", "30", "1", "30", "0", "120", "498", "1", "2026-10-20T09:00:00Z"]),
            Component, 720, new AppSettings(new NoCredentials()), heldBack, Now);

        Assert.Empty(findings);
        IgnoredSubject held = Assert.Single(heldBack);
        Assert.Equal("GET /api/teams/range", held.Subject);
        Assert.Equal(FindingKind.FailureRate, held.Kind);
        Assert.True(held.TooFewRecorded);
        Assert.Equal("only 1 recorded, too few to judge", held.Pattern);
    }

    [Fact]
    public void A_thinly_recorded_route_shown_on_request_is_judged_anyway()
    {
        AppSettings settings = new(new NoCredentials());
        settings.AlwaysShownSubjects.Add("GET /api/teams/range");

        ServiceFinding finding = Assert.Single(AppInsightsMonitor.FromRoutes(Routes(
            ["GET /api/teams/range", "30", "1", "30", "0", "120", "498", "1", "2026-10-20T09:00:00Z"]),
            Component, 720, settings, [], Now));

        Assert.Equal("1 recorded · at least 1 user · last failed 3h ago", finding.Context);
    }

    [Fact]
    public void A_route_that_is_neither_failing_nor_slow_is_not_held_back_for_being_quiet()
    {
        List<IgnoredSubject> heldBack = [];

        AppInsightsMonitor.FromRoutes(Routes(
            ["GET /api/teams/range", "30", "1", "0", "0", "120", "", "0", ""]),
            Component, 720, new AppSettings(new NoCredentials()), heldBack, Now);

        Assert.Empty(heldBack);
    }

    private static QueryTable Scanned(params string[][] rows) =>
        new(["name", "total", "recorded", "failed", "notFound", "unauthorized", "p95", "codes", "users", "lastFailed"], rows);

    [Fact]
    public void A_path_answering_mostly_401_to_a_few_calls_is_held_back_as_scanning()
    {
        List<IgnoredSubject> heldBack = [];

        List<ServiceFinding> findings = AppInsightsMonitor.FromRoutes(Scanned(
            ["POST /mcp/events", "97", "26", "97", "0", "97", "120", "401", "0", "2026-10-20T06:00:00Z"]),
            Component, 720, new AppSettings(new NoCredentials()), heldBack, Now);

        Assert.Empty(findings);
        IgnoredSubject held = Assert.Single(heldBack);
        Assert.Equal("mostly 404 or 401 and under 200 calls", held.Pattern);
    }

    [Fact]
    public void A_busy_route_answering_401_is_still_a_finding()
    {
        ServiceFinding finding = Assert.Single(AppInsightsMonitor.FromRoutes(Scanned(
            ["POST /api/session/updatepushtoken", "805", "47", "805", "0", "805", "120", "401", "0", "2026-10-20T10:00:00Z"]),
            Component, 720, new AppSettings(new NoCredentials()), [], Now));

        Assert.Equal(FindingKind.FailureRate, finding.Kind);
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
        List<ServiceFinding> findings = FromDependencies(Dependencies(
            ["Azure blob", "store.blob.core.windows.net", "1203", "240", "65", "12", "409, 412", "3", "2026-10-20T11:58:00Z"]));

        ServiceFinding finding = Assert.Single(findings);
        Assert.Equal("Azure blob store.blob.core.windows.net", finding.Subject);
        Assert.Equal("~65 of ~1,203 calls failed (5%), codes 409, 412", finding.Detail);
        Assert.Equal("12 failures recorded · at least 3 users · last failed 2m ago", finding.Context);
        Assert.Equal(65, finding.Magnitude);
    }

    [Fact]
    public void A_dependency_with_too_few_recorded_failures_is_held_back_and_says_why()
    {
        List<IgnoredSubject> heldBack = [];

        Assert.Empty(FromDependencies(Dependencies(
            ["HTTP", "registry.example.org", "300", "10", "58", "2", "400", "1", "2026-10-20T11:58:00Z"]), heldBack));

        IgnoredSubject held = Assert.Single(heldBack);
        Assert.Equal("HTTP registry.example.org", held.Subject);
        Assert.Equal(FindingKind.Dependency, held.Kind);
        Assert.Equal("only 2 failures recorded, too few to judge", held.Pattern);
    }

    private static QueryTable Exceptions(params string[][] rows) =>
        new(["problemId", "thrown", "recorded", "users", "operations", "operation", "lastThrown"], rows);

    [Fact]
    public void An_exception_thrown_outside_a_failed_request_is_a_finding_of_its_own()
    {
        ServiceFinding finding = Assert.Single(FromExceptions(Exceptions(
            ["System.NullReferenceException at Jobs.Sync.Run", "1240", "45", "12", "1", "SyncJob", "2026-10-20T11:55:00Z"])));

        Assert.Equal(FindingKind.Exception, finding.Kind);
        Assert.Equal("System.NullReferenceException at Jobs.Sync.Run", finding.Subject);
        Assert.Equal("~1,240 thrown in SyncJob", finding.Detail);
        Assert.Equal("45 recorded · at least 12 users · last thrown 5m ago", finding.Context);
    }

    [Fact]
    public void An_exception_thrown_from_several_operations_says_how_many()
    {
        ServiceFinding finding = Assert.Single(FromExceptions(Exceptions(
            ["System.TimeoutException at Data.Query", "30", "30", "0", "3", "GET Orders/Load", "2026-10-20T10:00:00Z"])));

        Assert.Equal("30 thrown across 3 operations", finding.Detail);
        Assert.Equal("last thrown 2h ago", finding.Context);
    }

    [Fact]
    public void An_exception_seen_in_too_few_recorded_items_is_held_back_and_says_why()
    {
        List<IgnoredSubject> heldBack = [];

        Assert.Empty(FromExceptions(Exceptions(
            ["System.TimeoutException at Data.Query", "90", "3", "1", "1", "SyncJob", "2026-10-20T10:00:00Z"]), heldBack));

        IgnoredSubject held = Assert.Single(heldBack);
        Assert.Equal(FindingKind.Exception, held.Kind);
        Assert.Equal("only 3 recorded, too few to judge", held.Pattern);
    }
}
