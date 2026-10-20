using System;
using Huginn.Models;
using Xunit;

namespace Huginn.Tests;

public sealed class SentryIssueItemTests
{
    [Fact]
    public void Reach_says_everyone_reached_and_since_when()
    {
        SentryIssueItem issue = new()
        {
            UserCount = 1353,
            FirstSeen = new DateTimeOffset(2024, 4, 14, 12, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("1,353 users since Apr 14, 2024", issue.Reach);
    }

    [Fact]
    public void Recent_reach_names_its_window()
    {
        SentryIssueItem issue = new() { RecentUserCount = 40 };

        Assert.Equal("40 in 14d", issue.RecentReach);
    }

    [Fact]
    public void The_subtitle_carries_both_reaches()
    {
        SentryIssueItem issue = new()
        {
            ShortId = "MOBILE-APP-4T7",
            ProjectSlug = "mobile-app",
            UserCount = 1,
            RecentUserCount = 1,
            FirstSeen = new DateTimeOffset(2024, 4, 14, 12, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("4T7 · 1 user since Apr 14, 2024 · 1 in 14d", issue.Subtitle);
    }

    [Theory]
    [InlineData("fatal", true)]
    [InlineData("error", true)]
    [InlineData("warning", false)]
    [InlineData("info", false)]
    public void Only_fatal_and_error_count_as_crashes(string level, bool crash)
    {
        Assert.Equal(crash, new SentryIssueItem { Level = level }.IsCrash);
    }
}
