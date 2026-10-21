using System;
using System.Collections.Generic;
using System.Linq;
using Huginn.Models;
using Huginn.Services;
using Xunit;

namespace Huginn.Tests;

[Collection(SettingsFile.Name)]
public sealed class SentryMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset April = new(2026, 4, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Settings for an install that has already checked Sentry once, ten minutes ago.</summary>
    private static AppSettings Checked() =>
        new(new NoCredentials()) { SentryCheckedAt = Now.AddMinutes(-10) };

    private static SentryIssueItem Issue(
        string id = "1",
        DateTimeOffset? firstSeen = null,
        int users = 1353,
        int recentUsers = 40,
        bool regressed = false) => new()
        {
            Id = id,
            ShortId = $"MOBILE-APP-{id}",
            ProjectSlug = "mobile-app",
            FirstSeen = firstSeen ?? April,
            LastSeen = Now.AddMinutes(-5),
            UserCount = users,
            EventCount = users * 2,
            RecentUserCount = recentUsers,
            RecentEventCount = recentUsers * 2,
            IsRegression = regressed,
        };

    [Fact]
    public void A_months_old_issue_that_was_missing_from_the_last_check_is_not_new()
    {
        AppSettings settings = Checked();

        var snapshot = new SentryMonitor().Evaluate([Issue(firstSeen: April)], settings, Now);

        Assert.Empty(snapshot.NewIssues);
        Assert.NotEqual("new", snapshot.Issues[0].FlagReason);
    }

    [Fact]
    public void An_issue_Sentry_first_saw_since_the_last_check_is_new()
    {
        AppSettings settings = Checked();

        var snapshot = new SentryMonitor().Evaluate([Issue(firstSeen: Now.AddMinutes(-5))], settings, Now);

        Assert.Single(snapshot.NewIssues);
        Assert.Equal("new", snapshot.Issues[0].FlagReason);
    }

    [Fact]
    public void The_first_check_raises_nothing_as_new()
    {
        AppSettings settings = new(new NoCredentials());

        var snapshot = new SentryMonitor().Evaluate([Issue(firstSeen: Now.AddMinutes(-5))], settings, Now);

        Assert.Empty(snapshot.NewIssues);
        Assert.Equal(Now, settings.SentryCheckedAt);
    }

    [Fact]
    public void A_regression_already_raised_is_not_raised_again_after_a_restart()
    {
        AppSettings settings = Checked();
        new SentryMonitor().Evaluate([Issue(regressed: true)], settings, Now);
        settings.DismissSentryIssue("1");

        var afterRestart = new SentryMonitor().Evaluate([Issue(regressed: true)], settings, Now.AddMinutes(10));

        Assert.Empty(afterRestart.Regressions);
        Assert.False(afterRestart.Issues[0].IsFlagged && afterRestart.Issues[0].FlagReason == "regressed");
    }

    [Fact]
    public void A_new_label_on_an_issue_older_than_the_window_is_taken_down()
    {
        AppSettings settings = Checked();
        settings.FlagSentryIssue("1", "new");

        var snapshot = new SentryMonitor().Evaluate([Issue(firstSeen: April)], settings, Now);

        Assert.NotEqual("new", snapshot.Issues[0].FlagReason);
        Assert.False(settings.IsSentryIssueFlagged("1"));
    }

    [Fact]
    public void Ranked_by_the_last_fortnight_a_crash_still_hitting_people_comes_first()
    {
        List<SentryIssueItem> issues =
        [
            Issue(id: "mostly-fixed", users: 895, recentUsers: 13),
            Issue(id: "still-going", users: 589, recentUsers: 90),
        ];

        Assert.Equal(["still-going", "mostly-fixed"], SentryMonitor.Ranked(issues, allTime: false).Select(i => i.Id));
    }

    [Fact]
    public void Ranked_over_all_time_the_crash_that_has_reached_most_people_comes_first()
    {
        List<SentryIssueItem> issues =
        [
            Issue(id: "busy-today", users: 100, recentUsers: 90),
            Issue(id: "long-running", users: 1353, recentUsers: 40),
        ];

        Assert.Equal(["long-running", "busy-today"], SentryMonitor.Ranked(issues, allTime: true).Select(i => i.Id));
    }

    [Fact]
    public void Reach_alone_raises_nothing()
    {
        AppSettings settings = Checked();

        var snapshot = new SentryMonitor().Evaluate([Issue(users: 50000, recentUsers: 50000)], settings, Now);

        Assert.False(snapshot.Issues[0].IsFlagged);
    }

    [Fact]
    public void A_mute_lifts_when_recent_reach_doubles_and_not_when_lifetime_reach_grows()
    {
        AppSettings settings = Checked();
        settings.MuteSentryIssue("1", 40, 80);

        var grownOverall = new SentryMonitor().Evaluate([Issue(users: 5000, recentUsers: 60)], settings, Now);
        Assert.True(settings.IsSentryIssueMuted("1"));
        Assert.Empty(grownOverall.Escalations);

        var doubledRecently = new SentryMonitor().Evaluate([Issue(users: 5000, recentUsers: 80)], settings, Now);
        Assert.False(settings.IsSentryIssueMuted("1"));
        Assert.Single(doubledRecently.Escalations);
    }

    [Fact]
    public void A_mute_saved_against_a_day_of_reach_is_measured_again_without_raising_it()
    {
        AppSettings settings = Checked();
        settings.MutedSentryIssues["1"] = new SentryAcknowledgement { UserCount = 5, EventCount = 6 };

        var snapshot = new SentryMonitor().Evaluate([Issue(recentUsers: 40)], settings, Now);

        Assert.True(settings.IsSentryIssueMuted("1"));
        Assert.Empty(snapshot.Escalations);
        Assert.Equal(40, settings.MutedSentryIssues["1"].UserCount);
    }
}
