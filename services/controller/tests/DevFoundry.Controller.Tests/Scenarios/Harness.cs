using System.Collections.Concurrent;
using System.Text.Json;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>
/// One Temporal dev server shared by the scenario tests. It is the real server binary the stack uses,
/// run locally inside the test image; without it (for example outside Docker) the scenarios are skipped.
/// </summary>
public sealed class TemporalFixture : IAsyncLifetime
{
    private const string Binary = "/usr/local/bin/temporal";

    public WorkflowEnvironment? Environment { get; private set; }

    public async Task InitializeAsync()
    {
        if (File.Exists(Binary))
        {
            Environment = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions
            {
                DevServerOptions = new() { ExistingPath = Binary },
            });
        }
    }

    public async Task DisposeAsync()
    {
        if (Environment is not null)
        {
            await Environment.ShutdownAsync();
        }
    }
}

[CollectionDefinition("temporal")]
public sealed class TemporalCollection : ICollectionFixture<TemporalFixture>;

/// <summary>An agent's reply as the sandbox would return it.</summary>
public sealed record AgentReply(SandboxRunResult Result)
{
    /// <summary>The model answered with this text (as OpenCode's JSON event stream).</summary>
    public static AgentReply Says(string text) => new(new SandboxRunResult(0, false, Events(text), "", false));

    public static AgentReply NotSignedIn { get; } = new(new SandboxRunResult(78, false, "", "OpenCode is not signed in", false));

    public static AgentReply Crashes { get; } = new(new SandboxRunResult(1, false, "", "boom", false));

    private static string Events(string text) => string.Join('\n',
        JsonSerializer.Serialize(new { type = "step_start" }),
        JsonSerializer.Serialize(new { type = "text", part = new { messageID = "m1", text } }),
        JsonSerializer.Serialize(new { type = "step_finish" }));
}

public sealed record AgentCall(string Role, string Prompt, string Image, bool Writable, bool ModelAuth);

/// <summary>
/// Everything the workflow talks to, scripted: agents, project checks, the workspace and Forgejo. It records
/// what the workflow did so tests can assert on it, and fails loudly if the workflow does something unscripted.
/// </summary>
public sealed class FakeWorld
{
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<AgentReply>> agents = [];
    private readonly Dictionary<string, Queue<SandboxRunResult>> checks = [];
    private readonly Queue<ChangeReport> changes = [];

    public List<string> Comments { get; } = [];
    public List<AgentCall> AgentCalls { get; } = [];
    public List<string> CheckCalls { get; } = [];
    public List<string> Problems { get; } = [];
    public List<PullRequestRequest> PullRequests { get; } = [];
    public List<PublishRequest> Publishes { get; } = [];
    public List<string> CleanedUp { get; } = [];
    public int ChangeChecks { get; private set; }

    /// <summary>The project's file; null means the repository has none.</summary>
    public string? ProjectConfig { get; set; } =
        """{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

    /// <summary>Files that exist in the workspace, for the evidence check. Null means every cited file exists.</summary>
    public HashSet<string>? ExistingFiles { get; set; }

    public Func<WorkspaceRequest, WorkspaceInfo>? Prepare { get; set; }
    public Func<PublishRequest, PublishResult?>? Publish { get; set; }

    public FakeWorld Agent(string role, params AgentReply[] replies)
    {
        agents[role] = new Queue<AgentReply>(replies);
        return this;
    }

    public FakeWorld Check(string command, params SandboxRunResult[] results)
    {
        checks[command] = new Queue<SandboxRunResult>(results);
        return this;
    }

    public FakeWorld Changes(params ChangeReport[] reports)
    {
        foreach (var report in reports)
        {
            changes.Enqueue(report);
        }
        return this;
    }

    public static ChangeReport OneFile() => new([new FileChange("hello.py", FileChange.Modified)], []);

    public static ChangeReport Rejected(params string[] violations) => new([new FileChange("x", FileChange.Added)], violations);

    public static SandboxRunResult Fails(string output, long exit = 1) => new(exit, false, output, "", false);

    public static SandboxRunResult Passes() => new(0, false, "ok", "", false);

    public IEnumerable<AgentCall> Calls(string role) => AgentCalls.Where(call => call.Role == role);

    public async Task WaitForCommentAsync(Func<string, bool> match, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < until)
        {
            lock (gate)
            {
                if (Comments.Any(match))
                {
                    return;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Expected comment never appeared. Comments so far:\n" + string.Join("\n---\n", Snapshot(Comments)));
    }

    public List<T> Snapshot<T>(List<T> list)
    {
        lock (gate)
        {
            return [.. list];
        }
    }

    // ---- the activities the workflow calls ---------------------------------------------------------------

    /// <summary>Stands in for ForgejoActivities and WorkspaceActivities; the names are what the workflow calls.</summary>
    private sealed class MainActivities(FakeWorld world)
    {
        [Activity("forgejo_post_issue_comment")]
        public void PostComment(CommentRequest request)
        {
            lock (world.gate) { world.Comments.Add(request.Body); }
        }

        [Activity("forgejo_create_pull_request")]
        public PullRequestInfo CreatePullRequest(PullRequestRequest request)
        {
            lock (world.gate) { world.PullRequests.Add(request); }
            return new PullRequestInfo(1, "http://forgejo/pulls/1");
        }

        [Activity("workspace_prepare")]
        public WorkspaceInfo Prepare(WorkspaceRequest request) =>
            world.Prepare?.Invoke(request) ?? new WorkspaceInfo(request.Name, $"/work/{request.Name}", request.Branch, "main", "abcdef1234567890");

        [Activity("workspace_cleanup")]
        public void Cleanup(string name)
        {
            lock (world.gate) { world.CleanedUp.Add(name); }
        }

        [Activity("workspace_read_project_config")]
        public string? ReadProjectConfig(string name) => world.ProjectConfig;

        [Activity("workspace_changes")]
        public ChangeReport DetectChanges(string name)
        {
            lock (world.gate)
            {
                world.ChangeChecks++;
                return world.changes.Count > 0 ? world.changes.Dequeue() : OneFile();
            }
        }

        [Activity("workspace_publish")]
        public PublishResult? Publish(PublishRequest request)
        {
            lock (world.gate) { world.Publishes.Add(request); }
            return world.Publish is null ? new PublishResult("c0ffee", "factory/issue-1", 1) : world.Publish(request);
        }
    }

    private sealed class SandboxActivities(FakeWorld world)
    {
        [Activity(SandboxNames.ActivityName)]
        public SandboxRunResult Run(SandboxRunRequest request) => world.Sandbox(request);
    }

    private SandboxRunResult Sandbox(SandboxRunRequest request)
    {
        var command = request.Command;
        lock (gate)
        {
            if (command is ["opencode", "run", ..])
            {
                var role = command[command.ToList().IndexOf("--agent") + 1];
                AgentCalls.Add(new AgentCall(role, command[^1], request.Image, request.WorkspaceWritable, request.NeedsModelAuth));
                if (agents.TryGetValue(role, out var queue) && queue.Count > 0)
                {
                    return queue.Dequeue().Result;
                }
                Problems.Add($"unscripted call of the {role} agent");
                return new SandboxRunResult(99, false, "", "unscripted", false);
            }

            // The evidence check: sh -c <script> sh <paths...>
            if (command is ["sh", "-c", _, "sh", ..])
            {
                var found = command.Skip(4).Where(path => ExistingFiles is null || ExistingFiles.Contains(path));
                return new SandboxRunResult(0, false, string.Join('\n', found.Select(path => $"found {path}")), "", false);
            }

            var key = string.Join(' ', command);
            CheckCalls.Add(key);
            return checks.TryGetValue(key, out var scripted) && scripted.Count > 0 ? scripted.Dequeue() : Passes();
        }
    }

    /// <summary>Runs the workflows and fake activities on the shared dev server while <paramref name="scenario"/> runs.</summary>
    public async Task<T> RunAsync<T>(TemporalFixture fixture, Func<ScenarioContext, Task<T>> scenario)
    {
        Skip.If(fixture.Environment is null, "no Temporal dev server in this environment");
        var client = fixture.Environment!.Client;
        var queue = "scenario-" + Guid.NewGuid().ToString("N");

        var main = new TemporalWorker(client, new TemporalWorkerOptions(queue)
            .AddWorkflow<FeatureWorkflow>().AddWorkflow<ForgejoEventWorkflow>().AddAllActivities(new MainActivities(this)));
        // The sandbox activities live on a queue of their own, as in production.
        var sandbox = new TemporalWorker(client, new TemporalWorkerOptions(SandboxNames.DefaultTaskQueue)
            .AddAllActivities(new SandboxActivities(this)));

        using (main)
        using (sandbox)
        {
            return await main.ExecuteAsync(() => sandbox.ExecuteAsync(() => scenario(new ScenarioContext(client, queue, this))));
        }
    }
}

public sealed class ScenarioContext(ITemporalClient client, string queue, FakeWorld world)
{
    public ITemporalClient Client { get; } = client;
    public string Queue { get; } = queue;
    public FakeWorld World { get; } = world;

    public static FeatureRequest Request(string title = "Add goodbye", string body = "Add a goodbye greeting.", IReadOnlyList<string>? labels = null)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new FeatureRequest("acme", repo, 1, title, body, "alice", labels ?? ["feature"]);
    }

    public Task<WorkflowHandle<FeatureWorkflow, FeatureResult>> StartAsync(FeatureRequest request) =>
        Client.StartWorkflowAsync((FeatureWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    /// <summary>Runs the workflow to the end and returns its result.</summary>
    public async Task<FeatureResult> RunToEndAsync(FeatureRequest request)
    {
        var handle = await StartAsync(request);
        return await ResultAsync(handle);
    }

    /// <summary>The workflow result, but a stuck workflow fails the test instead of hanging it.</summary>
    public static async Task<FeatureResult> ResultAsync(WorkflowHandle<FeatureWorkflow, FeatureResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }
}

/// <summary>Builders for the JSON answers the agents give.</summary>
public static class Answers
{
    public static string Analyst(
        string verdict = "pass", string[]? criteria = null, string[]? questions = null,
        int files = 2, bool api = false, bool multiple = false, bool data = false, bool dependency = false) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "reject" ? "Not buildable." : "Clear enough.",
            acceptance_criteria = criteria ?? (verdict == "pass" ? ["goodbye() returns the text.", "bye selects it."] : []),
            questions = questions ?? (verdict == "needs_clarification" ? ["Which text?"] : []),
            complexity = new { files_estimate = files, multiple_projects = multiple, public_api_change = api, data_model_change = data, new_dependency = dependency },
        });

    /// <summary>A coverage answer with one entry per status; every covered or partial one cites hello.py.</summary>
    public static string Coverage(params string[] statuses) =>
        JsonSerializer.Serialize(new
        {
            verdict = statuses.All(s => s == "covered") ? "fully_covered" : statuses.All(s => s == "missing") ? "not_covered" : "partly_covered",
            summary = "Checked the code.",
            criteria = statuses.Select((status, i) => new { index = i + 1, status, evidence = status == "missing" ? Array.Empty<string>() : ["hello.py"], note = "" }).ToArray(),
        });

    public static string Architect(string verdict = "pass", string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "reject" ? "Cannot be done." : "A small design.",
            approach = verdict == "pass" ? "Add goodbye() to hello.py." : "",
            components = verdict == "pass" ? new[] { new { path = "hello.py", change = "add goodbye()" } } : [],
            risks = Array.Empty<string>(),
            guidance = new[] { "Keep hello unchanged." },
            questions = questions ?? (verdict == "needs_decision" ? ["JSON file or SQLite?"] : []),
        });

    public static string Developer(string verdict = "done", string[]? notes = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "done" ? "Added goodbye() and the bye argument." : "Cannot continue.",
            notes = notes ?? (verdict == "blocked" ? ["A protected file must change."] : []),
        });
}
