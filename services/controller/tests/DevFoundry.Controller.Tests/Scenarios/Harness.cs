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
        JsonSerializer.Serialize(new
        {
            type = "step_finish",
            part = new { reason = "stop", cost = 0.01, tokens = new { total = 175, input = 100, output = 20, reasoning = 5, cache = new { read = 50, write = 0 } } },
        }));
}

public sealed record AgentCall(string Role, string Prompt, string Image, bool Writable, bool ModelAuth, string Network);

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
    public List<string> CheckNetworks { get; } = [];
    public List<string> EvidenceNetworks { get; } = [];
    public List<string> Problems { get; } = [];
    public List<string> Notices { get; } = [];

    /// <summary>What Forgejo says about the pull request being revised; the default is a pull request the factory opened with two line comments.</summary>
    public RevisionContext RevisionContext { get; set; } = FactoryPullRequest();

    /// <summary>When set, reading the pull request fails with this (as when Forgejo refuses the token).</summary>
    public Exception? RevisionContextFailure { get; set; }

    public List<WorkspaceRequest> PrepareRequests { get; } = [];

    public static RevisionContext FactoryPullRequest(
        string headRef = "factory/issue-7", string author = "factory-bot", string state = "open", bool merged = false,
        string headRepo = "acme/shop", int prior = 0, ReviewDetails? review = null, bool noReview = false) =>
        new(new PullRequestDetails(7, "Add goodbye", "Closes #3", state, merged, headRef, "abc123", headRepo, "acme/shop", "main", author),
            "factory-bot", noReview ? null : review ?? new ReviewDetails(11, "bob", "REQUEST_CHANGES", "Please fix these",
            [new ReviewComment(1, "bob", "hello.py", 4, "Rename this function."), new ReviewComment(2, "bob", "test_hello.py", 9, "This test does not assert the text.")]), prior);

    /// <summary>Tickets the workflow opened, and an error to throw instead (as when the label is missing).</summary>
    public List<CreateIssueRequest> CreatedIssues { get; } = [];
    public Exception? IssueFailure { get; set; }

    /// <summary>How many times the reproduction test was fingerprinted, and what the hash is at call number n (1-based).</summary>
    public int Fingerprints { get; private set; }
    public Func<int, string>? FingerprintOf { get; set; }

    /// <summary>Called inside the sandbox activity just before a scripted agent answers, so a test can act while a stage runs.</summary>
    public Action<string>? BeforeAgent { get; set; }
    public List<PullRequestRequest> PullRequests { get; } = [];
    public List<PublishRequest> Publishes { get; } = [];
    public List<string> CleanedUp { get; } = [];
    public int ChangeChecks { get; private set; }

    /// <summary>The project's file; null means the repository has none.</summary>
    public string? ProjectConfig { get; set; } =
        """{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

    /// <summary>Files that exist in the workspace, for the evidence check. Null means every cited file exists.</summary>
    public HashSet<string>? ExistingFiles { get; set; }

    /// <summary>The diff the reviewers are given.</summary>
    public string Diff { get; set; } = "diff --git a/hello.py b/hello.py\n--- a/hello.py\n+++ b/hello.py\n+def goodbye():\n+    return 'Goodbye, world!'\n";

    public Func<WorkspaceRequest, WorkspaceInfo>? Prepare { get; set; }
    public Func<PublishRequest, PublishResult?>? Publish { get; set; }

    /// <summary>What saving unfinished work returns; by default it saves two files to factory/issue-1-wip.</summary>
    public Func<RescueRequest, RescueResult>? Rescue { get; set; }

    /// <summary>When set, the check of what changed fails with this, as an unexpected infrastructure error would.</summary>
    public Exception? ChangesFailure { get; set; }

    public List<RescueRequest> Rescues { get; } = [];

    /// <summary>Every step the workflow asked to keep for the committed record.</summary>
    public List<RecordEntry> Records { get; } = [];

    /// <summary>When set, opening the pull request fails with this.</summary>
    public Exception? PullRequestFailure { get; set; }

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

    /// <summary>What the reproducer changed: a new test file, plus anything else the test wants to show.</summary>
    public static ChangeReport TestOnly(params string[] others) =>
        new([new FileChange("test_hello.py", FileChange.Added), .. others.Select(path => new FileChange(path, FileChange.Modified))], []);

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

        [Activity("forgejo_read_revision_context")]
        public RevisionContext ReadRevisionContext(RevisionRequest request) =>
            world.RevisionContextFailure is { } failure ? throw failure : world.RevisionContext;

        [Activity("forgejo_create_issue")]
        public IssueInfo CreateIssue(CreateIssueRequest request)
        {
            if (world.IssueFailure is not null)
            {
                throw world.IssueFailure;
            }
            lock (world.gate)
            {
                world.CreatedIssues.Add(request);
                return new IssueInfo(90 + world.CreatedIssues.Count, $"http://forgejo/issues/{90 + world.CreatedIssues.Count}");
            }
        }

        [Activity("forgejo_post_notice_once")]
        public bool PostNotice(CommentRequest request)
        {
            lock (world.gate)
            {
                if (world.Notices.Any(n => n == request.Body))
                {
                    return false; // the real activity skips a notice that is already among the latest comments
                }
                world.Notices.Add(request.Body);
                world.Comments.Add(request.Body);
                return true;
            }
        }

        [Activity("forgejo_create_pull_request")]
        public PullRequestInfo CreatePullRequest(PullRequestRequest request)
        {
            if (world.PullRequestFailure is not null)
            {
                throw world.PullRequestFailure;
            }
            lock (world.gate) { world.PullRequests.Add(request); }
            return new PullRequestInfo(1, "http://forgejo/pulls/1");
        }

        [Activity("workspace_prepare")]
        public WorkspaceInfo Prepare(WorkspaceRequest request)
        {
            lock (world.gate) { world.PrepareRequests.Add(request); }
            return world.Prepare?.Invoke(request) ?? new WorkspaceInfo(request.Name, $"/work/{request.Name}", request.Branch, "main", "abcdef1234567890");
        }

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
            if (world.ChangesFailure is not null)
            {
                throw world.ChangesFailure;
            }
            lock (world.gate)
            {
                world.ChangeChecks++;
                return world.changes.Count > 0 ? world.changes.Dequeue() : OneFile();
            }
        }

        [Activity("workspace_rescue")]
        public RescueResult RescueWork(RescueRequest request)
        {
            lock (world.gate) { world.Rescues.Add(request); }
            return world.Rescue is null
                ? new RescueResult(true, "factory/issue-1-wip", 2, [], "", "http://ui/run")
                : world.Rescue(request);
        }

        [Activity("workspace_record")]
        public void Record(RecordEntry entry)
        {
            lock (world.gate) { world.Records.Add(entry); }
        }

        [Activity("workspace_diff")]
        public string DiffOf(string name) => world.Diff;

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
                BeforeAgent?.Invoke(role);
                AgentCalls.Add(new AgentCall(role, command[^1], request.Image, request.WorkspaceWritable, request.NeedsModelAuth, request.Network));
                if (agents.TryGetValue(role, out var queue) && queue.Count > 0)
                {
                    return queue.Dequeue().Result;
                }
                Problems.Add($"unscripted call of the {role} agent");
                return new SandboxRunResult(99, false, "", "unscripted", false);
            }

            // The fingerprint of the reproduction test: sha256sum -- <paths...>
            if (command is ["sha256sum", "--", ..])
            {
                Fingerprints++;
                var hash = FingerprintOf?.Invoke(Fingerprints) ?? new string('a', 64);
                return new SandboxRunResult(0, false, string.Join('\n', command.Skip(2).Select(path => $"{hash}  {path}")), "", false);
            }

            // The evidence check: sh -c <script> sh <paths...>
            if (command is ["sh", "-c", _, "sh", ..])
            {
                EvidenceNetworks.Add(request.Network);
                var found = command.Skip(4).Where(path => ExistingFiles is null || ExistingFiles.Contains(path));
                return new SandboxRunResult(0, false, string.Join('\n', found.Select(path => $"found {path}")), "", false);
            }

            var key = string.Join(' ', command);
            CheckCalls.Add(key);
            CheckNetworks.Add(request.Network);
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
            .AddWorkflow<FeatureWorkflow>().AddWorkflow<BugWorkflow>().AddWorkflow<IncidentWorkflow>().AddWorkflow<PullRequestRevisionWorkflow>().AddWorkflow<ForgejoEventWorkflow>().AddAllActivities(new MainActivities(this)));
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

    public Task<WorkflowHandle<BugWorkflow, FeatureResult>> StartBugAsync(FeatureRequest request) =>
        Client.StartWorkflowAsync((BugWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public static RevisionRequest Revision(string trigger = RevisionRequest.ByReview, string instruction = "Please fix these", string author = "bob")
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new RevisionRequest("acme", repo, 7, trigger, instruction, author);
    }

    public Task<WorkflowHandle<PullRequestRevisionWorkflow, FeatureResult>> StartRevisionAsync(RevisionRequest request) =>
        Client.StartWorkflowAsync((PullRequestRevisionWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public async Task<FeatureResult> RunRevisionAsync(RevisionRequest request)
    {
        var handle = await StartRevisionAsync(request);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public Task<WorkflowHandle<IncidentWorkflow, FeatureResult>> StartIncidentAsync(FeatureRequest request) =>
        Client.StartWorkflowAsync((IncidentWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public static async Task<FeatureResult> ResultAsync(WorkflowHandle<IncidentWorkflow, FeatureResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    /// <summary>Runs the ticket's workflow (feature, bug or incident, by its kind) to the end and returns its result.</summary>
    public async Task<FeatureResult> RunToEndAsync(FeatureRequest request)
    {
        if (request.IsIncident)
        {
            return await ResultAsync(await StartIncidentAsync(request));
        }
        if (request.IsBug)
        {
            return await ResultAsync(await StartBugAsync(request));
        }
        var handle = await StartAsync(request);
        return await ResultAsync(handle);
    }

    public static async Task<FeatureResult> ResultAsync(WorkflowHandle<BugWorkflow, FeatureResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
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

    public static string BugAnalyst(string verdict = "pass", string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = "repeat() returns one repetition too few.",
            expected = "repeat('ab', 3) returns 'ababab'",
            actual = "repeat('ab', 3) returns 'abab'",
            hints = new[] { "see repeat() in hello.py" },
            severity = "medium",
            questions = questions ?? (verdict == "needs_clarification" ? ["Which input fails?"] : []),
        });

    public static string Reproduction(string verdict = "reproduced", string[]? command = null, string[]? files = null, string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "reproduced" ? "The test calls repeat('ab', 3) and expects 'ababab'." : "I could not make it fail.",
            test_command = verdict == "reproduced" ? command ?? ["python", "-m", "pytest", "-q", "test_hello.py"] : [],
            test_files = verdict == "reproduced" ? files ?? ["test_hello.py"] : [],
            questions = questions ?? (verdict == "cannot_reproduce" ? ["Which input fails?"] : []),
        });

    public static string Diagnosis(string verdict = "pass", string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            root_cause = "repeat() multiplies by times - 1 instead of times.",
            locations = new[] { new { path = "hello.py", note = "the multiplication in repeat()" } },
            fix_outline = "Multiply by times.",
            questions = questions ?? (verdict == "needs_clarification" ? ["Is 0 allowed?"] : []),
        });

    public static string IncidentAssessment(string verdict = "pass", string severity = "high", string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = "Checkout requests fail with 500 errors since the last deploy.",
            severity,
            impact = "Customers cannot pay; about 30% of checkouts fail.",
            timeline = new[] { "10:40 deploy", "10:42 error rate rose" },
            evidence_gaps = new[] { "Which environment is affected" },
            questions = questions ?? (verdict == "needs_clarification" ? ["Which environment?"] : []),
        });

    public static string Investigation(string verdict = "pass", object[]? hypotheses = null, string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = "The retry change in hello.py is the best lead.",
            hypotheses = verdict == "pass"
                ? hypotheses ?? [new { cause = "The new retry loop doubles the load.", confidence = "medium", evidence = new[] { "hello.py" }, commits = new[] { "a1b2c3d" } }]
                : [],
            questions = questions ?? (verdict == "needs_clarification" ? ["Which service logs the 500s?"] : []),
        });

    public static string Mitigation(string verdict = "pass", int options = 2, int recommended = 1, string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = "Roll back first, fix later.",
            options = verdict == "pass"
                ? Enumerable.Range(1, options).Select(i => new
                {
                    kind = i == 1 ? "rollback" : "feature_flag",
                    title = $"Option {i} title",
                    steps = new[] { $"Step A of option {i}", $"Step B of option {i}" },
                    risk = "low",
                    verify = "Error rate drops below 1%.",
                    reversible = true,
                }).ToArray()
                : [],
            recommended = verdict == "pass" ? recommended : 0,
            questions = questions ?? (verdict == "needs_clarification" ? ["Is there a feature flag?"] : []),
        });

    public static string Postmortem(string verdict = "done") =>
        JsonSerializer.Serialize(new { verdict, summary = "A bad deploy caused failed checkouts; rolled back after 40 minutes." });

    /// <summary>The pull request resolver's answer: one response per comment, with the given statuses (fixed, answered, declined, needs_clarification).</summary>
    public static string Revision(string verdict = "done", params string[] statuses) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "done" ? "Addressed the comments." : "Cannot continue.",
            responses = verdict == "done"
                ? (statuses.Length == 0 ? ["fixed", "fixed", "fixed"] : statuses).Select((status, i) => new { index = i + 1, status, note = status == "fixed" ? "" : $"Reason for comment {i + 1}." }).ToArray()
                : [],
            notes = Array.Empty<string>(),
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

    /// <summary>A review answer; the verdict follows from the findings, as a real reviewer's must.</summary>
    public static string Review(params (string Severity, string Message)[] findings) =>
        JsonSerializer.Serialize(new
        {
            verdict = findings.Any(f => f.Severity is "blocker" or "major") ? "needs_changes" : "pass",
            summary = findings.Length == 0 ? "Looks right." : "Some things to look at.",
            findings = findings.Select(f => new { severity = f.Severity, path = "hello.py", message = f.Message }).ToArray(),
        });

    /// <summary>A QA answer with one entry per status; met criteria cite test_hello.py.</summary>
    public static string Qa(params string[] statuses) =>
        JsonSerializer.Serialize(new
        {
            verdict = statuses.All(s => s == "met") ? "pass" : "needs_changes",
            summary = statuses.All(s => s == "met") ? "Every criterion holds." : "Something is missing.",
            criteria = statuses.Select((status, i) => new
            {
                index = i + 1,
                status,
                evidence = status == "met" ? new[] { "test_hello.py" } : Array.Empty<string>(),
                note = status == "met" ? "" : "no test checks this",
            }).ToArray(),
        });

    public static string Developer(string verdict = "done", string[]? notes = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "done" ? "Added goodbye() and the bye argument." : "Cannot continue.",
            notes = notes ?? (verdict == "blocked" ? ["A protected file must change."] : []),
        });
}
