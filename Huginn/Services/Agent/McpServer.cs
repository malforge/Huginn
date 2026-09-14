using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Huginn.Services.Agent;

/// <summary>
/// Serves the snapshot to agents over stdio as an MCP server, started with <c>--mcp</c>.
/// </summary>
/// <remarks>
/// Works on the JSON document rather than the typed models: it keeps answering unchanged when the
/// snapshot gains fields, and stays clear of reflection-based serialisation, which a trimmed
/// build does not support.
/// </remarks>
public static class McpServer
{
    private const string ServerName = "huginn";

    /// <summary>Reads requests until stdin closes. Returns the process exit code.</summary>
    public static int Run()
    {
        Stream stdout = Console.OpenStandardOutput();
        StreamWriter writer = new(stdout, new UTF8Encoding(false)) { AutoFlush = true };
        TextReader reader = Console.In;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;

            JsonNode? response;
            try
            {
                response = Handle(JsonNode.Parse(line));
            }
            catch (Exception ex)
            {
                response = Error(null, -32603, ex.Message);
            }

            // A notification carries no id and takes no reply.
            if (response != null) writer.WriteLine(response.ToJsonString());
        }

        return 0;
    }

    private static JsonNode? Handle(JsonNode? request)
    {
        if (request is not JsonObject call) return null;

        string method = call["method"]?.GetValue<string>() ?? "";
        JsonNode? id = call["id"]?.DeepClone();

        if (id == null) return null;

        return method switch
        {
            "initialize" => Result(id, Initialize(call)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
            "tools/call" => Result(id, CallTool(call["params"] as JsonObject)),
            _ => Error(id, -32601, $"Unknown method '{method}'"),
        };
    }

    private static JsonObject Initialize(JsonObject call)
    {
        // Echo the version the client asked for. Every version this server has anything to say
        // about behaves the same way here, and disagreeing about it just ends the session.
        string version = (call["params"] as JsonObject)?["protocolVersion"]?.GetValue<string>()
                         ?? "2024-11-05";

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = Str(ReadSnapshot(), "huginnVersion"),
            },
        };
    }

    /// <summary>
    /// Built through the constructor rather than a collection expression. The latter resolves to
    /// the generic <c>JsonArray.Add</c>, which is not trim safe and warns in a published build.
    /// </summary>
    private static JsonArray Tools() =>
        new(
        Tool("huginn_status",
            "What Huginn currently says needs attention: source health, raised crashes, service "
            + "findings and the pull request queues. Start here.",
            new JsonObject()),

        Tool("huginn_crashes",
            "Sentry issues, worst reach first. Returns JSON.",
            new JsonObject
            {
                ["onlyRaised"] = Prop("boolean", "Only issues Huginn raised. Default true."),
                ["includeMuted"] = Prop("boolean", "Include muted issues. Default false."),
                ["limit"] = Prop("integer", "Maximum issues to return. Default 20."),
            }),

        Tool("huginn_services",
            "Application Insights findings, most severe first. Returns JSON.",
            new JsonObject
            {
                ["kind"] = Prop("string", "Filter: FailureRate, Latency, Dependency or NoTraffic."),
                ["includeMuted"] = Prop("boolean", "Include muted findings. Default false."),
                ["limit"] = Prop("integer", "Maximum findings to return. Default 20."),
            }),

        Tool("huginn_pull_requests",
            "Pull requests Huginn is tracking. Returns JSON.",
            new JsonObject
            {
                ["queue"] = Prop("string",
                    "Filter: awaiting-review, missing-reviewers, failed-validation, "
                    + "autocomplete-off, mine or acknowledged."),
            }),

        Tool("huginn_builds", "Failing and retrying builds. Returns JSON.", new JsonObject()),

        Tool("huginn_refresh",
            "Ask a running Huginn to poll its sources now and wait for the new snapshot. Use when "
            + "the snapshot is stale, or after doing something you expect to show up. Azure DevOps "
            + "is always re-read; Sentry and Application Insights only if their interval elapsed.",
            new JsonObject
            {
                ["waitSeconds"] = Prop("integer", "How long to wait for Huginn. Default 30."),
            }));

    private static JsonObject Tool(string name, string description, JsonObject properties) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(),
        },
    };

    private static JsonObject Prop(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject CallTool(JsonObject? parameters)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? "";
        JsonObject args = parameters?["arguments"] as JsonObject ?? [];

        if (name == "huginn_refresh") return Text(Refresh(args));

        JsonNode? snapshot = ReadSnapshot();
        if (snapshot == null)
        {
            return Text(
                "Huginn has never written a snapshot. Either it has not run since this was "
                + "installed, or it is configured under a different HUGINN_PROFILE.");
        }

        string freshness = Freshness(snapshot);
        string gap = Environment.NewLine + Environment.NewLine;

        return name switch
        {
            "huginn_status" => Text(freshness + gap + Status(snapshot)),
            "huginn_crashes" => Text(freshness + gap + Crashes(snapshot, args)),
            "huginn_services" => Text(freshness + gap + Services(snapshot, args)),
            "huginn_pull_requests" => Text(freshness + gap + PullRequests(snapshot, args)),
            "huginn_builds" => Text(freshness + gap + Dump(Array(snapshot, "builds"))),
            _ => Text($"Unknown tool '{name}'."),
        };
    }

    /// <summary>
    /// Asks the running Huginn to poll, then waits for the snapshot to be rewritten. Only a newer
    /// timestamp proves anything: if Huginn is not running there is nobody to answer, and saying
    /// so is more use than a cheerful reply over unchanged data.
    /// </summary>
    private static string Refresh(JsonObject args)
    {
        int seconds = Count(args, "waitSeconds", 30);
        string before = Str(ReadSnapshot(), "generatedAt");

        SnapshotStore.RequestRefresh();

        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);

            JsonNode? snapshot = ReadSnapshot();
            string now = Str(snapshot, "generatedAt");

            if (now.Length == 0 || now == before) continue;

            return Freshness(snapshot!) + Environment.NewLine + Environment.NewLine
                   + Status(snapshot!);
        }

        return $"Huginn did not answer within {seconds}s, so the snapshot below is unchanged. It "
               + "is probably not running; the request has been left for it to pick up when it "
               + "next starts." + Environment.NewLine + Environment.NewLine
               + (ReadSnapshot() is { } stale
                   ? Freshness(stale) + Environment.NewLine + Environment.NewLine + Status(stale)
                   : "No snapshot has ever been written.");
    }

    private static JsonNode? ReadSnapshot()
    {
        try
        {
            return File.Exists(SnapshotStore.Path)
                ? JsonNode.Parse(File.ReadAllText(SnapshotStore.Path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How old the answer is. Everything else is only as true as Huginn's last poll, and a reader
    /// that does not know the age cannot tell a quiet system from a stopped one.
    /// </summary>
    private static string Freshness(JsonNode snapshot)
    {
        string raw = Str(snapshot, "generatedAt");
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
            return "Snapshot age unknown.";

        TimeSpan age = DateTimeOffset.UtcNow - at;
        string ago = age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";

        string warning = age.TotalMinutes > 45
            ? "  (stale: Huginn may not be running, so read this as history rather than now)"
            : "";

        return $"Huginn snapshot written {ago}{warning}.";
    }

    private static JsonArray Array(JsonNode snapshot, string name) =>
        snapshot[name] as JsonArray ?? [];

    private static string Status(JsonNode snapshot)
    {
        StringBuilder text = new();

        text.AppendLine("SOURCES");
        foreach (JsonNode? source in Array(snapshot, "sources"))
        {
            if (source == null) continue;
            string flag = Bool(source, "needsAttention") ? "   <-- needs attention" : "";
            text.AppendLine($"  {Str(source, "name")}: {Str(source, "state")} "
                            + $"{Str(source, "message")}{flag}");
        }

        JsonArray crashes = Array(snapshot, "crashes");
        List<JsonNode?> raised =
            [.. crashes.Where(c => Bool(c, "raised") && !Bool(c, "muted"))];

        text.AppendLine();
        text.AppendLine($"CRASHES  ({raised.Count} raised of {crashes.Count} unresolved)");
        foreach (JsonNode? crash in raised.Take(10))
        {
            text.AppendLine($"  [{Str(crash, "raisedReason")}] {Str(crash, "shortId")} "
                            + $"{Str(crash, "title")}");
            text.AppendLine($"      {Num(crash, "events")} events, {Num(crash, "users")} users"
                            + Suffix(crash, "trend", " - ") + Suffix(crash, "releases", " - "));
        }

        JsonArray services = Array(snapshot, "services");
        List<JsonNode?> active = [.. services.Where(f => !Bool(f, "muted"))];

        text.AppendLine();
        text.AppendLine($"SERVICES  ({active.Count} findings)");
        foreach (JsonNode? finding in active.Take(10))
        {
            text.AppendLine($"  [{Str(finding, "kind")}] {Str(finding, "resource")} "
                            + $"{Str(finding, "subject")}");
            text.AppendLine($"      {Str(finding, "detail")}{Suffix(finding, "trend", " - ")}");
        }

        text.AppendLine();
        text.AppendLine("PULL REQUESTS");
        foreach (IGrouping<string, JsonNode?> queue in Array(snapshot, "pullRequests")
                     .GroupBy(pr => Str(pr, "queue")))
        {
            text.AppendLine($"  {queue.Key}: {queue.Count()}");
            foreach (JsonNode? pr in queue.Take(5))
                text.AppendLine($"      !{Num(pr, "id")} {Str(pr, "title")} "
                                + $"({Str(pr, "repository")})");
        }

        JsonArray builds = Array(snapshot, "builds");
        if (builds.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("BUILDS");
            foreach (JsonNode? build in builds)
                text.AppendLine($"  [{Str(build, "status")}] {Str(build, "definition")} "
                                + $"on {Str(build, "branch")}");
        }

        return text.ToString().TrimEnd();
    }

    private static string Crashes(JsonNode snapshot, JsonObject args)
    {
        bool onlyRaised = Flag(args, "onlyRaised", true);
        bool includeMuted = Flag(args, "includeMuted", false);
        int limit = Count(args, "limit", 20);

        return Dump([.. Array(snapshot, "crashes")
            .Where(c => !onlyRaised || Bool(c, "raised"))
            .Where(c => includeMuted || !Bool(c, "muted"))
            .Take(limit)
            .Select(c => c?.DeepClone())]);
    }

    private static string Services(JsonNode snapshot, JsonObject args)
    {
        string kind = args["kind"]?.GetValue<string>() ?? "";
        bool includeMuted = Flag(args, "includeMuted", false);
        int limit = Count(args, "limit", 20);

        return Dump([.. Array(snapshot, "services")
            .Where(f => kind.Length == 0
                        || string.Equals(Str(f, "kind"), kind, StringComparison.OrdinalIgnoreCase))
            .Where(f => includeMuted || !Bool(f, "muted"))
            .Take(limit)
            .Select(f => f?.DeepClone())]);
    }

    private static string PullRequests(JsonNode snapshot, JsonObject args)
    {
        string queue = args["queue"]?.GetValue<string>() ?? "";

        return Dump([.. Array(snapshot, "pullRequests")
            .Where(pr => queue.Length == 0
                         || string.Equals(Str(pr, "queue"), queue, StringComparison.OrdinalIgnoreCase))
            .Select(pr => pr?.DeepClone())]);
    }

    private static string Dump(JsonArray items) =>
        items.Count == 0
            ? "[]"
            : items.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string Str(JsonNode? node, string name)
    {
        JsonNode? value = node?[name];
        return value == null ? "" : value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToString();
    }

    private static bool Bool(JsonNode? node, string name) =>
        node?[name]?.GetValueKind() == JsonValueKind.True;

    private static long Num(JsonNode? node, string name) =>
        node?[name] is { } value && long.TryParse(value.ToString(), out long parsed) ? parsed : 0;

    private static string Suffix(JsonNode? node, string name, string separator)
    {
        string value = Str(node, name);
        return value.Length == 0 ? "" : separator + value;
    }

    private static bool Flag(JsonObject args, string name, bool fallback)
    {
        JsonNode? node = args[name];
        return node == null ? fallback : node.GetValueKind() == JsonValueKind.True;
    }

    private static int Count(JsonObject args, string name, int fallback)
    {
        JsonNode? node = args[name];
        return node != null && int.TryParse(node.ToString(), out int parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static JsonObject Text(string body) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body }),
    };

    private static JsonObject Result(JsonNode? id, JsonNode payload) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = payload,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
