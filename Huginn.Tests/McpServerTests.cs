using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Services;
using Huginn.Services.Agent;
using Xunit;

namespace Huginn.Tests;

/// <summary>
/// The tools as an agent calls them, over a snapshot written where the running app writes it,
/// with this test standing in for the app on the other side of the investigation files.
/// </summary>
[Collection(SettingsFile.Name)]
public sealed class McpServerTests : IDisposable
{
    public McpServerTests()
    {
        Directory.CreateDirectory(AppSettings.SettingsDir);
        InvestigationStore.DiscardOlderThan(TimeSpan.Zero);
    }

    public void Dispose()
    {
        InvestigationStore.DiscardOlderThan(TimeSpan.Zero);
        try { File.Delete(SnapshotStore.Path); } catch { }
    }

    private static void WriteSnapshot(params JsonObject[] services)
    {
        JsonObject snapshot = new()
        {
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["services"] = new JsonArray([.. services]),
        };
        File.WriteAllText(SnapshotStore.Path, snapshot.ToJsonString());
    }

    private static JsonObject Finding(string subject, string detail = "50% of ~1,000 calls failed") => new()
    {
        ["kind"] = "FailureRate",
        ["resource"] = "prod-insights",
        ["subject"] = subject,
        ["detail"] = detail,
        ["muted"] = false,
        ["series"] = new JsonArray(1.0, 2.0, 3.0),
        ["trend"] = "steady all window",
        ["portalUrl"] = "https://portal.azure.com/#blade/very/long/link",
    };

    private static string Call(string tool, JsonObject arguments)
    {
        JsonObject request = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        };

        JsonNode response = JsonNode.Parse(McpServer.Respond(request.ToJsonString())!)!;
        return response["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    /// <summary>Plays the running app: takes each question as it arrives and answers it.</summary>
    private static Task AnswerQuestions(int count, Func<JsonObject, string?> answer, CancellationToken ct) =>
        Task.Run(() =>
        {
            int answered = 0;
            while (answered < count && !ct.IsCancellationRequested)
            {
                foreach (string path in Directory.EnumerateFiles(
                             AppSettings.SettingsDir, InvestigationStore.RequestFilter))
                {
                    if (InvestigationStore.Read(path) is not { } question) continue;
                    InvestigationStore.Discard(path);
                    answered++;

                    if (answer(question) is { } text)
                        InvestigationStore.Answer(question["id"]!.GetValue<string>(), text);
                }
                Thread.Sleep(50);
            }
        }, ct);

    [Fact]
    public void Services_leave_out_the_series_and_portal_link_unless_asked_for_them()
    {
        WriteSnapshot(Finding("GET /v2/Orders/{id}"));

        string compact = Call("huginn_services", []);
        string full = Call("huginn_services", new JsonObject { ["full"] = true });

        Assert.DoesNotContain("portalUrl", compact);
        Assert.DoesNotContain("\"series\"", compact);
        Assert.Contains("steady all window", compact);
        Assert.Contains("portalUrl", full);
        Assert.Contains("\"series\"", full);
    }

    [Fact]
    public void A_subject_spelled_exactly_as_given_is_that_finding_beside_one_differing_only_in_case()
    {
        WriteSnapshot(Finding("POST /v2/Customers"), Finding("POST /v2/customers"));
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task app = AnswerQuestions(1, q => "answered " + q["subject"]!.GetValue<string>(), stop.Token);

        string answer = Call("huginn_investigate",
            new JsonObject { ["subject"] = "POST /v2/customers", ["waitSeconds"] = 5 });

        Assert.Equal("answered POST /v2/customers", answer);
        app.Wait(stop.Token);
    }

    [Fact]
    public void A_route_is_found_by_any_spelling_of_it()
    {
        WriteSnapshot(Finding("POST /v2/Customers"));
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task app = AnswerQuestions(1, q => "answered " + q["subject"]!.GetValue<string>(), stop.Token);

        string answer = Call("huginn_investigate",
            new JsonObject { ["subject"] = "post /v2/customers/", ["waitSeconds"] = 5 });

        Assert.Equal("answered POST /v2/Customers", answer);
        app.Wait(stop.Token);
    }

    [Fact]
    public void Findings_a_subject_cannot_tell_apart_are_listed_by_their_own_subjects_and_details()
    {
        WriteSnapshot(Finding("POST /v2/Customers", "76% of ~80,000 calls failed"),
                      Finding("POST /v2/customers", "40% of ~83 calls failed"));

        string answer = Call("huginn_investigate", new JsonObject { ["subject"] = "post /V2/CUSTOMERS" });

        Assert.Contains("more than once", answer);
        Assert.Contains("POST /v2/Customers (76% of ~80,000 calls failed)", answer);
        Assert.Contains("POST /v2/customers (40% of ~83 calls failed)", answer);
    }

    [Fact]
    public void Agents_asking_at_the_same_time_each_get_their_own_answer()
    {
        WriteSnapshot(Finding("GET /v2/Orders/{id}"), Finding("GET /v2/Invoices/{id}"));
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        Task app = AnswerQuestions(2, q => "about " + q["subject"]!.GetValue<string>(), stop.Token);

        Task<string> orders = Task.Run(() => Call("huginn_investigate",
            new JsonObject { ["subject"] = "GET /v2/Orders/{id}", ["waitSeconds"] = 10 }));
        Task<string> invoices = Task.Run(() => Call("huginn_investigate",
            new JsonObject { ["subject"] = "GET /v2/Invoices/{id}", ["waitSeconds"] = 10 }));

        Assert.Equal("about GET /v2/Orders/{id}", orders.Result);
        Assert.Equal("about GET /v2/Invoices/{id}", invoices.Result);
        app.Wait(stop.Token);
    }

    [Fact]
    public void A_question_huginn_took_but_has_not_answered_yet_is_collected_by_asking_again()
    {
        WriteSnapshot(Finding("GET /v2/Orders/{id}"));
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Task app = AnswerQuestions(1, _ => null, stop.Token);

        string answer = Call("huginn_investigate",
            new JsonObject { ["subject"] = "GET /v2/Orders/{id}", ["waitSeconds"] = 2 });

        Assert.Contains("still running", answer);
        Assert.Contains("again", answer);
        app.Wait(stop.Token);
    }

    [Fact]
    public void A_question_nobody_took_says_huginn_is_probably_not_running_and_is_withdrawn()
    {
        WriteSnapshot(Finding("GET /v2/Orders/{id}"));

        string answer = Call("huginn_investigate",
            new JsonObject { ["subject"] = "GET /v2/Orders/{id}", ["waitSeconds"] = 1 });

        Assert.Contains("probably not running", answer);
        Assert.Empty(Directory.EnumerateFiles(AppSettings.SettingsDir, InvestigationStore.RequestFilter));
    }

    [Fact]
    public void A_wait_longer_than_a_client_allows_is_cut_to_what_it_allows()
    {
        WriteSnapshot(Finding("GET /v2/Orders/{id}"));
        DateTime started = DateTime.UtcNow;

        string answer = Call("huginn_investigate",
            new JsonObject { ["subject"] = "GET /v2/Orders/{id}", ["waitSeconds"] = 180 });

        Assert.Contains("within 50s", answer);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 49, 58);
    }
}
