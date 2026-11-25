using System.Collections.Concurrent;
using System.Text.Json;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace AiSdlcFactory.Controller.Tests.Scenarios;

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
public sealed record AgentReply(SandboxRunResult Result, Exception? Failure = null)
{
    /// <summary>The sandbox activity itself fails: Docker is unavailable, the factory's machinery and not the agent.</summary>
    public static AgentReply DockerDown { get; } = new(new SandboxRunResult(0, false, "", "", false),
        new Temporalio.Exceptions.ApplicationFailureException("starting the sandbox failed: docker is down", errorType: "Docker", nonRetryable: true));

    /// <summary>The model answered with this text (as OpenCode's JSON event stream).</summary>
    public static AgentReply Says(string text, long input = 100) => new(new SandboxRunResult(0, false, Events(text, input), "", false));

    /// <summary>The model provider refuses the call because its usage limit is reached: OpenCode exits with an error and says so.</summary>
    public static AgentReply UsageLimitReached { get; } = new(new SandboxRunResult(1, false, "", "Error: You have hit your usage limit. Try again later.", false));

    public static AgentReply NotSignedIn { get; } = new(new SandboxRunResult(78, false, "", "OpenCode is not signed in", false));

    public static AgentReply Crashes { get; } = new(new SandboxRunResult(1, false, "", "boom", false));

    /// <summary>OpenCode could not find the role file: it warns, exits 0 and answers as its default agent (here with a reply the parser would reject).</summary>
    public static AgentReply RoleMissing(string role) => new(new SandboxRunResult(0, false, Events("{\"verdict\":\"ready\"}"), $"!  agent \"{role}\" not found. Falling back to default agent", false));

    private static string Events(string text, long input = 100) => string.Join('\n',
        JsonSerializer.Serialize(new { type = "step_start" }),
        JsonSerializer.Serialize(new { type = "text", part = new { messageID = "m1", text } }),
        JsonSerializer.Serialize(new
        {
            type = "step_finish",
            part = new { reason = "stop", cost = 0.01, tokens = new { total = input + 75, input, output = 20, reasoning = 5, cache = new { read = 50, write = 0 } } },
        }));
}

public sealed record AgentCall(string Role, string Prompt, string Image, bool Writable, bool ModelAuth, string Network);

/// <summary>
/// Everything the workflow talks to, scripted: agents, project checks, the workspace and the git host. It records
/// what the workflow did so tests can assert on it, and fails loudly if the workflow does something unscripted.
/// </summary>
public sealed class FakeWorld
{
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<AgentReply>> agents = [];
    private readonly Dictionary<string, Queue<SandboxRunResult>> checks = [];
    private readonly Dictionary<string, Exception> checkFailures = [];
    private readonly Queue<Func<FakeWorld, ChangeReport>> changes = [];

    public List<string> Comments { get; } = [];
    public List<AgentCall> AgentCalls { get; } = [];
    public List<string> CheckCalls { get; } = [];
    public List<string> CheckNetworks { get; } = [];
    public List<string> EvidenceNetworks { get; } = [];
    public List<string> Problems { get; } = [];
    public List<string> Notices { get; } = [];
    public List<ReviewRequestInput> ReviewRequests { get; } = [];
    public List<ProtectedPathsRequest> ProtectedPathRequests { get; } = [];

    /// <summary>The repository's own sandbox image definition; null means it has none.</summary>
    public RepoImageSource? RepoImage { get; set; }
    public Exception? RepoImageFailure { get; set; }
    public SandboxBuildResult BuildResult { get; set; } = new("repo-0123456789ab", true, 12.3, 500L * 1024 * 1024, "");
    public Exception? BuildFailure { get; set; }
    public List<SandboxBuildRequest> Builds { get; } = [];
    public FactoryDefaults Defaults { get; set; } = FactoryDefaults.Standard;

    /// <summary>What the git host says about the pull request being revised; the default is a pull request the factory opened with two line comments.</summary>
    public RevisionContext RevisionContext { get; set; } = FactoryPullRequest();

    /// <summary>When set, reading the pull request fails with this (as when the host refuses the token).</summary>
    public Exception? RevisionContextFailure { get; set; }

    /// <summary>What the git host says about the pull request a /qa run is for; the default is a pull request the factory opened for ticket 7.</summary>
    public QaContext QaContext { get; set; } = new(FactoryPullRequest(body: "## Acceptance criteria\n- goodbye() returns the text.\n- bye selects it.\n").Pull, "factory-bot");

    /// <summary>The newest open pull request of the factory for the ticket (what the host would find); null means the ticket has none.</summary>
    public int? OpenFactoryPull { get; set; }

    /// <summary>What the git host says about the pull request an /update is for; the default is a pull request the factory opened for ticket 7, based on main.</summary>
    public UpdateContext UpdateContext { get; set; } = new(FactoryPullRequest().Pull, "factory-bot");

    /// <summary>What the git host says about the pull request a /review is for: by default a person's open pull request with two changed files and a short diff.</summary>
    public Exception? ReviewContextFailure { get; set; }

    public ReviewContext ReviewContext { get; set; } = new(
        FactoryPullRequest(author: "alice", headRef: "alice/goodbye").Pull,
        new PullChanges([new ChangedFile("hello.py", "modified", 4, 1), new ChangedFile("test_hello.py", "added", 9, 0)], 2, "diff --git a/hello.py b/hello.py\n+def goodbye():\n+    return 'Goodbye'\n", false));

    /// <summary>What merging the base branch does: by default it goes through without conflicts and brings three files.</summary>
    public MergeResult Merge { get; set; } = new(MergeResult.Merged, "b4a5e6f7c8d90123", [], 3);

    /// <summary>When set, merging fails with this (as when git cannot merge).</summary>
    public Exception? MergeFailure { get; set; }

    /// <summary>When set, pushing the merge fails with this.</summary>
    public Exception? PublishMergeFailure { get; set; }

    public List<MergeBaseRequest> MergeRequests { get; } = [];
    public List<PublishMergeRequest> MergePublishes { get; } = [];
    private readonly Queue<IReadOnlyList<string>> markerChecks = [];

    /// <summary>The files that still hold conflict markers at each check; once the list runs out, none do.</summary>
    public FakeWorld Markers(params string[][] lists)
    {
        foreach (var list in lists)
        {
            markerChecks.Enqueue(list);
        }
        return this;
    }

    /// <summary>The stage results kept between runs, by workflow id, as the real store keeps them in a file per ticket.</summary>
    public Dictionary<string, ResumeState> Resumes { get; } = [];

    /// <summary>The workflow ids whose saved stages were looked up (a lookup happens only for a run started by /retry).</summary>
    public List<string> ResumeLoads { get; } = [];

    /// <summary>The runs added to the usage log.</summary>
    public List<UsageLogEntry> UsageLogs { get; } = [];

    /// <summary>What the usage log says the finished runs used today, for the daily token limits.</summary>
    public UsageToday UsageToday { get; set; } = new(0, 0);

    /// <summary>What the usage log says a run of any kind has typically used here; the default is no history.</summary>
    public UsageEstimate Estimate { get; set; } = new(0, 0);

    /// <summary>The kinds the runs asked an estimate for.</summary>
    public List<string> EstimateKinds { get; } = [];

    /// <summary>How many times the daily totals were looked up (once before every agent run, when a daily limit is set).</summary>
    public int UsageTodayChecks { get; private set; }

    /// <summary>What the weekly report activity returns: by default a report with two findings.</summary>
    public ImprovementReportResult ImprovementReport { get; set; } = new("# Factory report, 2026-W46\n\n## What repeated (2)\n\n- **Rejected answers: the analyst agent.**\n", 12, 2, "/var/tmp/reports/2026-W46.md");

    /// <summary>When set, making the report fails with this.</summary>
    public Exception? ImprovementReportFailure { get; set; }

    public List<ImprovementRequest> ImprovementRequests { get; } = [];

    /// <summary>What the usage log says about the week's pull requests, and what the host says became of them; the report gets the second.</summary>
    /// <summary>How the host's tickets say which workflow to run; by default only the plain labels.</summary>
    public TriggerRules Triggers { get; set; } = TriggerRules.Default;

    /// <summary>What the check of who may steer the factory answers (everyone, by default), and who it was asked about.</summary>
    public CommandVerdict CommandVerdict { get; set; } = CommandVerdict.Yes;
    public List<CommandCheck> CommandChecks { get; } = [];
    public StartVerdict StartVerdict { get; set; } = StartVerdict.Yes;
    public List<StartCheck> StartChecks { get; } = [];

    public PullScope PullScope { get; set; } = new(["acme/shop"], [], []);
    public IReadOnlyList<PullOutcome> PullOutcomes { get; set; } = [];
    public Exception? PullOutcomesFailure { get; set; }
    public List<IReadOnlyList<PullOutcome>?> ReportOutcomes { get; } = [];

    public List<WorkspaceRequest> PrepareRequests { get; } = [];

    public static RevisionContext FactoryPullRequest(
        string headRef = "factory/issue-7", string author = "factory-bot", string state = "open", bool merged = false,
        string headRepo = "acme/shop", int prior = 0, ReviewDetails? review = null, bool noReview = false, string body = "Closes #3") =>
        new(new PullRequestDetails(7, "Add goodbye", body, state, merged, headRef, "abc123", headRepo, "acme/shop", "main", author),
            "factory-bot", noReview ? [] : [review ?? new ReviewDetails(11, "bob", "REQUEST_CHANGES", "Please fix these",
            [new ReviewComment(1, "bob", "hello.py", 4, "Rename this function.", "@@ -1,3 +1,3 @@\n def hello():"), new ReviewComment(2, "bob", "test_hello.py", 9, "This test does not assert the text.")])], prior);

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
    public List<PublishSettingsRequest> SettingsPublishes { get; } = [];
    public List<string> CleanedUp { get; } = [];
    public int ChangeChecks { get; private set; }

    /// <summary>The project's file; null means the repository has none.</summary>
    public string? ProjectConfig { get; set; } =
        """{"image":"python","gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

    /// <summary>The project's house rules (<c>.factory/instructions.md</c>); null means it has none.</summary>
    public InstructionsFile? Instructions { get; set; }

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

    /// <summary>The sandbox activity for this check fails instead of returning a result.</summary>
    public FakeWorld CheckThrows(string command, Exception failure)
    {
        checkFailures[command] = failure;
        return this;
    }

    public List<string> RemovedOversized { get; } = [];

    /// <summary>The files the default branch changed since the earlier run, as the resume check is told; null means it cannot say.</summary>
    public IReadOnlyList<string>? ChangedSinceFiles { get; set; }

    public FakeWorld Changes(params ChangeReport[] reports)
    {
        foreach (var report in reports)
        {
            changes.Enqueue(_ => report);
        }
        return this;
    }

    /// <summary>
    /// What the next checks of the workspace find, worked out when they run, from what the workflow has told the agents so far. For a result that depends on the
    /// workflow's own clock (the postmortem's file name has today's date in it) this is the only way a test cannot be wrong around midnight.
    /// </summary>
    public FakeWorld ChangesFrom(params Func<FakeWorld, ChangeReport>[] makers)
    {
        foreach (var maker in makers)
        {
            changes.Enqueue(maker);
        }
        return this;
    }

    /// <summary>The file the workflow last told the postmortem writer to write, as the prompt names it; null before the writer has been asked.</summary>
    public string? WriterPath { get; private set; }

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

    /// <summary>Stands in for GitHostActivities and WorkspaceActivities; the names are what the workflow calls.</summary>
    private sealed class MainActivities(FakeWorld world)
    {
        [Activity("githost_post_issue_comment")]
        public void PostComment(CommentRequest request)
        {
            lock (world.gate) { world.Comments.Add(request.Body); }
        }

        [Activity("githost_read_revision_context")]
        public RevisionContext ReadRevisionContext(RevisionRequest request) =>
            world.RevisionContextFailure is { } failure ? throw failure : world.RevisionContext;

        [Activity("githost_read_qa_context")]
        public QaContext ReadQaContext(QaRequest request) => world.QaContext;

        [Activity("githost_read_review_context")]
        public ReviewContext ReadReviewContext(PullReviewRequest request) => world.ReviewContextFailure is { } failure ? throw failure : world.ReviewContext;

        [Activity("githost_read_update_context")]
        public UpdateContext ReadUpdateContext(UpdateRequest request) => world.UpdateContext;

        [Activity("githost_find_factory_pull_request")]
        public int? FindFactoryPullRequest(TicketReference ticket) => world.OpenFactoryPull;

        [Activity("githost_triggers")]
        public TriggerRules ReadTriggers(string hostName) => world.Triggers;

        [Activity("githost_check_command")]
        public CommandVerdict CheckCommand(CommandCheck check)
        {
            lock (world.gate)
            {
                world.CommandChecks.Add(check);
            }
            return world.CommandVerdict;
        }

        [Activity("githost_check_start")]
        public StartVerdict CheckStart(StartCheck check)
        {
            lock (world.gate)
            {
                world.StartChecks.Add(check);
            }
            return world.StartVerdict;
        }

        [Activity("workspace_pull_scope")]
        public PullScope ReadPullScope(ImprovementRequest request) => world.PullScope;

        [Activity("githost_pull_outcomes")]
        public IReadOnlyList<PullOutcome> ReadPullOutcomes(PullOutcomeRequest request) => world.PullOutcomesFailure is { } failure ? throw failure : world.PullOutcomes;

        [Activity("workspace_improvement_report")]
        public ImprovementReportResult BuildImprovementReport(ImprovementRequest request, IReadOnlyList<PullOutcome>? pullOutcomes)
        {
            lock (world.gate) { world.ImprovementRequests.Add(request); world.ReportOutcomes.Add(pullOutcomes); }
            return world.ImprovementReportFailure is { } failure ? throw failure : world.ImprovementReport;
        }

        [Activity("workspace_usage_estimate")]
        public UsageEstimate EstimateUsage(string repository, string kind)
        {
            lock (world.gate) { world.EstimateKinds.Add(kind); }
            return world.Estimate;
        }

        [Activity("workspace_usage_today")]
        public UsageToday UsageToday(string repository)
        {
            lock (world.gate) { world.UsageTodayChecks++; }
            return world.UsageToday;
        }

        [Activity("workspace_log_usage")]
        public void LogUsage(UsageLogEntry entry)
        {
            lock (world.gate) { world.UsageLogs.Add(entry); }
        }

        [Activity("workspace_save_resume")]
        public void SaveResume(SaveResumeRequest request)
        {
            lock (world.gate) { world.Resumes[request.Name] = request.State; }
        }

        [Activity("workspace_changed_since")]
        public IReadOnlyList<string>? ChangedSince(string name, string fromCommit) => world.ChangedSinceFiles;

        [Activity("workspace_load_resume")]
        public ResumeState? LoadResume(string name)
        {
            lock (world.gate)
            {
                world.ResumeLoads.Add(name);
                return world.Resumes.GetValueOrDefault(name);
            }
        }

        [Activity("workspace_clear_resume")]
        public void ClearResume(string name)
        {
            lock (world.gate) { world.Resumes.Remove(name); }
        }

        [Activity("workspace_merge_base")]
        public MergeResult MergeBase(MergeBaseRequest request)
        {
            lock (world.gate) { world.MergeRequests.Add(request); }
            return world.MergeFailure is { } failure ? throw failure : world.Merge;
        }

        [Activity("workspace_conflict_markers")]
        public IReadOnlyList<string> ConflictMarkers(string name)
        {
            lock (world.gate) { return world.markerChecks.Count > 0 ? world.markerChecks.Dequeue() : []; }
        }

        [Activity("workspace_publish_merge")]
        public PublishResult PublishMerge(PublishMergeRequest request)
        {
            lock (world.gate) { world.MergePublishes.Add(request); }
            if (world.PublishMergeFailure is { } failure)
            {
                throw failure;
            }
            return new PublishResult("deadbeef12345678", "factory/issue-7", 1);
        }

        [Activity("githost_request_reviewers")]
        public bool RequestReviewers(ReviewRequestInput request)
        {
            lock (world.gate) { world.ReviewRequests.Add(request); }
            return true;
        }

        [Activity("githost_create_issue")]
        public IssueInfo CreateIssue(CreateIssueRequest request)
        {
            if (world.IssueFailure is not null)
            {
                throw world.IssueFailure;
            }
            lock (world.gate)
            {
                world.CreatedIssues.Add(request);
                return new IssueInfo(90 + world.CreatedIssues.Count, $"http://git.test/issues/{90 + world.CreatedIssues.Count}");
            }
        }

        [Activity("githost_post_notice_once")]
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

        [Activity("githost_post_notice_unique")]
        public bool PostUniqueNotice(CommentRequest request)
        {
            lock (world.gate)
            {
                if (world.Comments.Any(c => c.Contains(request.Body, StringComparison.Ordinal)))
                {
                    return false; // the real activity skips a notice whose exact text is anywhere on the ticket
                }
                world.Notices.Add(request.Body);
                world.Comments.Add(request.Body);
                return true;
            }
        }

        [Activity("githost_create_pull_request")]
        public PullRequestInfo CreatePullRequest(PullRequestRequest request)
        {
            if (world.PullRequestFailure is not null)
            {
                throw world.PullRequestFailure;
            }
            lock (world.gate) { world.PullRequests.Add(request); }
            return new PullRequestInfo(1, "http://git.test/pulls/1");
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

        [Activity("workspace_read_project_image")]
        public RepoImageSource? ReadProjectImage(string name) => world.RepoImageFailure is { } failure ? throw failure : world.RepoImage;

        [Activity("factory_defaults")]
        public FactoryDefaults GetDefaults() => world.Defaults;

        [Activity("workspace_set_protected_paths")]
        public void SetProtectedPaths(ProtectedPathsRequest request)
        {
            lock (world.gate) { world.ProtectedPathRequests.Add(request); }
        }

        [Activity("workspace_read_project_instructions")]
        public InstructionsFile? ReadProjectInstructions(string name) => world.Instructions;

        [Activity("workspace_read_project_config")]
        public string? ReadProjectConfig(string name) => world.ProjectConfig;

        [Activity("workspace_remove_oversized")]
        public IReadOnlyList<string> RemoveOversized(RemoveOversizedRequest request)
        {
            lock (world.gate)
            {
                world.RemovedOversized.AddRange(request.Paths);
            }
            return request.Paths;
        }

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
                return world.changes.Count > 0 ? world.changes.Dequeue()(world) : OneFile();
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

        [Activity("workspace_publish_settings")]
        public PublishResult PublishSettings(PublishSettingsRequest request)
        {
            lock (world.gate) { world.SettingsPublishes.Add(request); }
            return new PublishResult("c0ffee", "factory/setup-3", 1);
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

        [Activity(SandboxNames.BuildActivityName)]
        public SandboxBuildResult Build(SandboxBuildRequest request)
        {
            lock (world.gate) { world.Builds.Add(request); }
            return world.BuildFailure is { } failure ? throw failure : world.BuildResult;
        }
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
                if (role == "postmortem-writer" && System.Text.RegularExpressions.Regex.Match(command[^1], @"docs/postmortems/[A-Za-z0-9._-]+\.md") is { Success: true } named)
                {
                    WriterPath = named.Value;
                }
                AgentCalls.Add(new AgentCall(role, command[^1], request.Image, request.WorkspaceWritable, request.NeedsModelAuth, request.Network));
                if (agents.TryGetValue(role, out var queue) && queue.Count > 0)
                {
                    var reply = queue.Dequeue();
                    return reply.Failure is { } failure ? throw failure : reply.Result;
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
            if (checkFailures.TryGetValue(key, out var checkFailure))
            {
                throw checkFailure;
            }
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
            .AddWorkflow<FeatureWorkflow>().AddWorkflow<BugWorkflow>().AddWorkflow<IncidentWorkflow>().AddWorkflow<PlanWorkflow>().AddWorkflow<PullRequestRevisionWorkflow>().AddWorkflow<PullRequestUpdateWorkflow>().AddWorkflow<PullRequestReviewWorkflow>().AddWorkflow<DocsWorkflow>().AddWorkflow<AskWorkflow>().AddWorkflow<SetupWorkflow>().AddWorkflow<ImprovementWorkflow>().AddWorkflow<QaWorkflow>().AddWorkflow<RepoEventWorkflow>().AddAllActivities(new MainActivities(this)));
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

    public static WorkRequest Request(string title = "Add goodbye", string body = "Add a goodbye greeting.", IReadOnlyList<string>? labels = null)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new WorkRequest("acme", repo, 1, title, body, "alice", labels ?? ["feature"]);
    }

    public Task<WorkflowHandle<FeatureWorkflow, WorkResult>> StartAsync(WorkRequest request) =>
        Client.StartWorkflowAsync((FeatureWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public Task<WorkflowHandle<BugWorkflow, WorkResult>> StartBugAsync(WorkRequest request) =>
        Client.StartWorkflowAsync((BugWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public static RevisionRequest Revision(string trigger = RevisionRequest.ByReview, string instruction = "Please fix these", string author = "bob")
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new RevisionRequest("acme", repo, 7, trigger, instruction, author);
    }

    public static UpdateRequest Update(bool onPullRequest = true)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new UpdateRequest("acme", repo, 7, onPullRequest, "bob");
    }

    public async Task<WorkResult> RunUpdateAsync(UpdateRequest request)
    {
        var handle = await Client.StartWorkflowAsync((PullRequestUpdateWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public static PullReviewRequest PullReview()
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new PullReviewRequest("acme", repo, 7, "bob");
    }

    public static AskRequest Ask(string question = "Where is the greeting built?", bool onPullRequest = false)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new AskRequest("acme", repo, 7, onPullRequest, "bob", question, "Say goodbye", "As a user I want a goodbye.");
    }

    /// <summary>A <c>/setup</c> comment on ticket 3 of a repository that has no settings.</summary>
    public static WorkRequest Setup() => new("acme", "r" + Guid.NewGuid().ToString("N")[..10], 3, "Connect this repository", "", "alice", Kind: WorkIds.Setup);

    public async Task<WorkResult> RunSetupAsync(WorkRequest request)
    {
        var handle = await Client.StartWorkflowAsync((SetupWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public async Task<WorkResult> RunAskAsync(AskRequest request)
    {
        var handle = await Client.StartWorkflowAsync((AskWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public async Task<WorkResult> RunPullReviewAsync(PullReviewRequest request)
    {
        var handle = await Client.StartWorkflowAsync((PullRequestReviewWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public static ImprovementRequest Improvement(string? repository = "acme/factory") => new("2026-W46", "2026-11-09", "2026-11-16", repository);

    public async Task<WorkResult> RunImprovementAsync(ImprovementRequest request)
    {
        var handle = await Client.StartWorkflowAsync((ImprovementWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId + "-" + Guid.NewGuid().ToString("N")[..8], Queue));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public static QaRequest Qa(string? suite = null, bool onPullRequest = false)
    {
        var repo = "r" + Guid.NewGuid().ToString("N")[..10];
        return new QaRequest("acme", repo, 7, onPullRequest, suite, "bob");
    }

    public Task<WorkflowHandle<QaWorkflow, WorkResult>> StartQaAsync(QaRequest request) =>
        Client.StartWorkflowAsync((QaWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public async Task<WorkResult> RunQaAsync(QaRequest request)
    {
        var handle = await Client.StartWorkflowAsync((QaWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public Task<WorkflowHandle<PullRequestRevisionWorkflow, WorkResult>> StartRevisionAsync(RevisionRequest request) =>
        Client.StartWorkflowAsync((PullRequestRevisionWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public async Task<WorkResult> RunRevisionAsync(RevisionRequest request)
    {
        var handle = await StartRevisionAsync(request);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public Task<WorkflowHandle<PlanWorkflow, WorkResult>> StartPlanAsync(WorkRequest request) =>
        Client.StartWorkflowAsync((PlanWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public static async Task<WorkResult> ResultAsync(WorkflowHandle<PlanWorkflow, WorkResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public static async Task<WorkResult> ResultAsync(WorkflowHandle<QaWorkflow, WorkResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    public Task<WorkflowHandle<IncidentWorkflow, WorkResult>> StartIncidentAsync(WorkRequest request) =>
        Client.StartWorkflowAsync((IncidentWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));

    public static async Task<WorkResult> ResultAsync(WorkflowHandle<IncidentWorkflow, WorkResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    /// <summary>Runs the ticket's workflow (feature, bug or incident, by its kind) to the end and returns its result.</summary>
    public async Task<WorkResult> RunToEndAsync(WorkRequest request)
    {
        if (request.IsIncident)
        {
            return await ResultAsync(await StartIncidentAsync(request));
        }
        if (request.IsPlan)
        {
            return await ResultAsync(await StartPlanAsync(request));
        }
        if (request.IsDocs)
        {
            var docs = await Client.StartWorkflowAsync((DocsWorkflow workflow) => workflow.RunAsync(request), new WorkflowOptions(request.WorkflowId, Queue));
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            return await docs.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
        }
        if (request.IsBug)
        {
            return await ResultAsync(await StartBugAsync(request));
        }
        var handle = await StartAsync(request);
        return await ResultAsync(handle);
    }

    public static async Task<WorkResult> ResultAsync(WorkflowHandle<BugWorkflow, WorkResult> handle)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await handle.GetResultAsync(rpcOptions: new RpcOptions { CancellationToken = limit.Token });
    }

    /// <summary>The workflow result, but a stuck workflow fails the test instead of hanging it.</summary>
    public static async Task<WorkResult> ResultAsync(WorkflowHandle<FeatureWorkflow, WorkResult> handle)
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

    /// <summary>A plan of three tickets: 1 and 2 wait for nothing, 3 waits for 1.</summary>
    public static string Plan(string verdict = "pass", int tickets = 3, string[]? questions = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "reject" ? "This is one small change, not a plan." : "Split into storage, API and screen.",
            tickets = verdict == "pass"
                ? Enumerable.Range(1, tickets).Select(i => new
                {
                    title = $"Part {i}",
                    summary = $"What part {i} does.",
                    acceptance_criteria = new[] { $"Criterion A of part {i}", $"Criterion B of part {i}" },
                    depends_on = i == 3 ? new[] { 1 } : Array.Empty<int>(),
                }).ToArray()
                : [],
            questions = questions ?? (verdict == "needs_clarification" ? ["Which database?"] : []),
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

    /// <summary>The merge resolver's answer: one resolution for each of the given conflicted files.</summary>
    public static string Merge(string verdict = "done", params string[] paths) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "done" ? "Resolved the merge." : "Cannot continue.",
            resolutions = verdict == "done" ? paths.Select(path => new { path, how = "combined", note = $"Kept both changes in {path}." }).ToArray() : [],
            notes = Array.Empty<string>(),
        });

    /// <summary>The documentation writer's answer.</summary>
    public static string Docs(string verdict = "done") =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict switch { "done" => "Wrote AGENTS.md from the code.", "unchanged" => "The documents are already right.", _ => "Cannot continue." },
            notes = Array.Empty<string>(),
        });

    /// <summary>The configurer's answer: a python project with a lint and a test check, unless <paramref name="config"/> says otherwise.</summary>
    public static string Setup(string verdict = "configured", string? config = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "configured" ? "A Python project tested with pytest and linted with ruff." : "I cannot tell how this project is built.",
            config = verdict == "configured" ? JsonSerializer.Deserialize<JsonElement>(config ?? SetupConfig) : (JsonElement?)null,
            reasons = new { image = "pyproject.toml shows Python.", gates = "CI runs ruff and pytest." },
        });

    public const string SetupConfig = """{"image":"python","gates":[{"name":"lint","command":["ruff","check","."]},{"name":"test","command":["python","-m","pytest","-q"]}],"reviewers":["reviewer"],"protected_paths":["ruff.toml"]}""";

    /// <summary>The asker's answer.</summary>
    public static string Ask(string verdict = "answered", string answer = "The greeting is built in `hello.py`.", params string[] files) =>
        JsonSerializer.Serialize(new { verdict, answer, files });

    /// <summary>The improver's answer: one proposal for each target.</summary>
    public static string Improvement(string verdict = "proposals", params string[] targets) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "nothing" ? "Too little data to propose anything." : "The analyst's answers are often rejected; its prompt is unclear about the shape.",
            proposals = verdict == "nothing"
                ? []
                : (targets.Length == 0 ? ["agents/agents/analyst.md"] : targets).Select(target => new
                {
                    kind = "prompt",
                    target,
                    change = $"Say in {target} that the answer is one JSON object.",
                    evidence = "7 of 20 analyst answers (35%) were rejected.",
                    risk = "A longer prompt costs a few tokens on every run.",
                }).ToArray(),
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

    public static string Tester(string verdict = "tests_written", string suite = "api", string findings = "[]", string assessments = "[]") =>
        verdict == "blocked"
            ? "{\"verdict\":\"blocked\",\"summary\":\"The application does not run here.\"}"
            : "{\"verdict\":\"tests_written\",\"summary\":\"Tried the greeting endpoint.\",\"scenarios\":[{\"name\":\"goodbye returns the text\",\"suite\":\"" + suite + "\",\"covers\":[1]}],\"files\":[\"e2e/api/goodbye.test\"],\"findings\":" + findings + ",\"assessments\":" + assessments + "}";

    public static string Developer(string verdict = "done", string[]? notes = null) =>
        JsonSerializer.Serialize(new
        {
            verdict,
            summary = verdict == "done" ? "Added goodbye() and the bye argument." : "Cannot continue.",
            notes = notes ?? (verdict == "blocked" ? ["A protected file must change."] : []),
        });
}
