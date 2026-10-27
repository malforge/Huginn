using System;
using System.Text.Json;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

public sealed class SentryApiClientTests
{
    /// <summary>
    /// One issue as the organization issues endpoint returns it for a dated query: the top level
    /// counts only the queried window, and lifetime holds what Sentry itself shows.
    /// </summary>
    private const string Issue = """
        [{
          "id": "118085675",
          "shortId": "MOBILE-APP-4T7",
          "title": "ApplicationNotResponding: Background ANR",
          "level": "fatal",
          "project": { "slug": "mobile-app" },
          "count": "51",
          "userCount": 40,
          "firstSeen": "2026-10-06T09:55:13Z",
          "lastSeen": "2026-10-20T09:24:34Z",
          "lifetime": {
            "count": "2210",
            "userCount": 1353,
            "firstSeen": "2026-04-14T07:12:00Z",
            "lastSeen": "2026-10-20T09:24:34Z"
          },
          "stats": { "24h": [[1760950800, 3], [1760954400, 5]] },
          "assignedTo": { "type": "user", "id": "7", "name": "Kari Nordmann" }
        }]
        """;

    private static SentryIssueItem Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return Assert.Single(SentryApiClient.ParseIssues(doc.RootElement, ["mobile-app"]));
    }

    [Fact]
    public void Reach_is_the_lifetime_total_rather_than_the_queried_window()
    {
        SentryIssueItem issue = Parse(Issue);

        Assert.Equal(1353, issue.UserCount);
        Assert.Equal(2210, issue.EventCount);
    }

    [Fact]
    public void First_seen_is_when_Sentry_first_saw_it_rather_than_the_start_of_the_window()
    {
        SentryIssueItem issue = Parse(Issue);

        Assert.Equal(new DateTimeOffset(2026, 4, 14, 7, 12, 0, TimeSpan.Zero), issue.FirstSeen);
    }

    [Fact]
    public void The_window_counts_are_kept_as_recent_reach()
    {
        SentryIssueItem issue = Parse(Issue);

        Assert.Equal(40, issue.RecentUserCount);
        Assert.Equal(51, issue.RecentEventCount);
    }

    [Fact]
    public void The_sparkline_is_read_from_the_hourly_series()
    {
        SentryIssueItem issue = Parse(Issue);

        Assert.Equal([3.0, 5.0], issue.EventSeries);
        Assert.Equal(60, issue.SeriesBucketMinutes);
    }

    [Fact]
    public void Who_an_issue_is_assigned_to_is_read_from_the_list()
    {
        SentryIssueItem issue = Parse(Issue);

        Assert.Equal("7", issue.AssigneeId);
        Assert.Equal("Kari Nordmann", issue.AssigneeName);
        Assert.False(issue.AssigneeIsTeam);
    }

    [Fact]
    public void An_issue_assigned_to_a_team_says_so()
    {
        SentryIssueItem issue = Parse(Issue.Replace(
            "\"type\": \"user\", \"id\": \"7\", \"name\": \"Kari Nordmann\"",
            "\"type\": \"team\", \"id\": \"3\", \"name\": \"mobile\""));

        Assert.True(issue.AssigneeIsTeam);
        Assert.Equal("mobile", issue.AssigneeName);
    }

    [Fact]
    public void An_unassigned_issue_has_no_assignee()
    {
        SentryIssueItem issue = Parse(Issue.Replace(
            "\"assignedTo\": { \"type\": \"user\", \"id\": \"7\", \"name\": \"Kari Nordmann\" }",
            "\"assignedTo\": null"));

        Assert.Equal("", issue.AssigneeId);
        Assert.Equal("", issue.AssignedLine);
    }

    [Fact]
    public void An_issue_from_a_project_not_watched_is_left_out()
    {
        using JsonDocument doc = JsonDocument.Parse(Issue);

        Assert.Empty(SentryApiClient.ParseIssues(doc.RootElement, ["someone-else"]));
    }

    [Fact]
    public void The_next_page_is_followed_while_Sentry_says_it_has_results()
    {
        const string link =
            "<https://de.sentry.io/api/0/organizations/o/issues/?cursor=0:0:1>; rel=\"previous\"; results=\"false\"; cursor=\"0:0:1\", "
            + "<https://de.sentry.io/api/0/organizations/o/issues/?cursor=0:100:0>; rel=\"next\"; results=\"true\"; cursor=\"0:100:0\"";

        Assert.Equal("0:100:0", SentryApiClient.NextCursor(link));
    }

    [Fact]
    public void There_is_no_next_page_once_Sentry_says_it_has_no_more_results()
    {
        const string link =
            "<https://de.sentry.io/api/0/organizations/o/issues/?cursor=0:0:1>; rel=\"previous\"; results=\"true\"; cursor=\"0:0:1\", "
            + "<https://de.sentry.io/api/0/organizations/o/issues/?cursor=0:200:0>; rel=\"next\"; results=\"false\"; cursor=\"0:200:0\"";

        Assert.Null(SentryApiClient.NextCursor(link));
    }
}
