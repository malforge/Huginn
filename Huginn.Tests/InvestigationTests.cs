using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Huginn.Models;
using Huginn.Services;
using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

public sealed class InvestigationTests
{
    /// <summary>A sign-in that has lapsed, so every query fails before it leaves the machine.</summary>
    private sealed class LapsedSignIn : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) =>
            throw new InvalidOperationException("sign-in lapsed");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) =>
            throw new InvalidOperationException("sign-in lapsed");
    }

    [Fact]
    public async Task A_report_missing_a_query_fails_so_it_is_not_handed_out_again_but_keeps_what_it_says()
    {
        ServiceFinding finding = new()
        {
            Kind = FindingKind.FailureRate,
            Subject = "GET /v2/Orders/{id}",
            WindowMinutes = 60,
        };
        using AppInsightsApiClient client = new(new LapsedSignIn());

        InvestigationIncompleteException failed = await Assert.ThrowsAsync<InvestigationIncompleteException>(
            () => Investigation.RunAsync(finding, client, CancellationToken.None));

        Assert.Contains("RESULT CODES", failed.Report);
        Assert.Contains("sign-in lapsed", failed.Report);
    }
}
