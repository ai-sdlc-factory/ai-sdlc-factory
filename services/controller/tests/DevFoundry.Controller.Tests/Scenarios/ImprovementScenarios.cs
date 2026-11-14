using Microsoft.Extensions.Logging.Abstractions;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>The weekly evaluation: a report made by counting, a ticket for it, and proposals from a read-only agent that nothing ever applies.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class ImprovementScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FakeWorld Improving(params string[] answers) =>
        new FakeWorld().Agent("improver", (answers.Length == 0 ? [Answers.Improvement()] : answers).Select(Say).ToArray());

    [SkippableFact]
    public async Task A_report_with_findings_is_opened_as_a_ticket_and_the_improver_proposes_changes_on_it()
    {
        var world = Improving(Answers.Improvement("proposals", "agents/agents/analyst.md", "egress/profiles.json"));

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("improvement_posted", result.Status);
        Assert.Empty(world.Problems);
        // The ticket carries the report and no label, so no factory workflow starts on it.
        var issue = Assert.Single(world.CreatedIssues);
        Assert.Equal(("acme", "factory", "Factory report, 2026-W46"), (issue.Owner, issue.Repo, issue.Title));
        Assert.Equal(world.ImprovementReport.Markdown, issue.Body);
        Assert.Empty(issue.Labels);
        // The agent reads and cannot change anything: no writable workspace, model access only.
        var call = Assert.Single(world.Calls("improver"));
        Assert.False(call.Writable);
        Assert.Equal(SandboxNetworks.Model, call.Network);
        Assert.Contains("Rejected answers: the analyst agent.", call.Prompt);
        var proposals = Assert.Single(world.Comments, c => c.StartsWith("**Proposed changes for the factory, from the report for 2026-W46**"));
        Assert.Contains("1. **prompt**, `agents/agents/analyst.md`", proposals);
        Assert.Contains("2. **prompt**, `egress/profiles.json`", proposals);
        Assert.Contains("Why: 7 of 20 analyst answers (35%) were rejected.", proposals);
        Assert.Contains("What could get worse:", proposals);
        Assert.Contains("**Nothing here was applied, and nothing will be.**", proposals);
        // Nothing was written, published, opened or saved.
        Assert.Empty(world.Publishes);
        Assert.Empty(world.PullRequests);
        Assert.Empty(world.Rescues);
        Assert.Equal("factory/improvement-2026-w46", Assert.Single(world.PrepareRequests).Branch);
        Assert.False(Assert.Single(world.PrepareRequests).ExistingBranch);
    }

    [SkippableFact]
    public async Task A_proposal_that_names_a_file_that_does_not_exist_is_left_out_and_counted()
    {
        var world = Improving(Answers.Improvement("proposals", "agents/agents/analyst.md", "agents/agents/made-up.md"));
        world.ExistingFiles = ["agents/agents/analyst.md"];

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("improvement_posted", result.Status);
        var proposals = Assert.Single(world.Comments, c => c.StartsWith("**Proposed changes for the factory"));
        Assert.Contains("`agents/agents/analyst.md`", proposals);
        Assert.DoesNotContain("made-up.md", proposals);
        Assert.Contains("1 more proposal named a file that does not exist in the repository and was left out.", proposals);
    }

    [SkippableFact]
    public async Task When_no_named_file_exists_the_improver_is_told_once_and_then_nothing_is_posted()
    {
        var world = Improving(Answers.Improvement("proposals", "nope.md"), Answers.Improvement("proposals", "still-nope.md"));
        world.ExistingFiles = [];

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("improvement_failed", result.Status);
        Assert.Equal(2, world.Calls("improver").Count());
        Assert.Contains("None of the files you named exist in the repository: nope.md", world.Calls("improver").ElementAt(1).Prompt);
        Assert.DoesNotContain(world.Comments, c => c.StartsWith("**Proposed changes"));
        Assert.Contains(world.Comments, c => c.Contains("named no file that exists"));
    }

    [SkippableFact]
    public async Task An_improver_that_sees_nothing_to_propose_says_so_on_the_ticket()
    {
        var world = Improving(Answers.Improvement("nothing"));

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("improvement_posted", result.Status);
        Assert.Contains(world.Comments, c => c.StartsWith("**No change proposed for 2026-W46**") && c.Contains("Too little data to propose anything."));
    }

    [SkippableFact]
    public async Task Without_a_repository_the_report_is_only_saved()
    {
        var world = Improving();

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement(repository: null)));

        Assert.Equal("report_saved", result.Status);
        Assert.Empty(world.CreatedIssues);
        Assert.Empty(world.AgentCalls);
        Assert.Single(world.ImprovementRequests);
    }

    [SkippableFact]
    public async Task A_week_with_nothing_that_repeated_opens_no_ticket_and_costs_no_tokens()
    {
        var world = Improving();
        world.ImprovementReport = world.ImprovementReport with { Findings = 0 };

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("report_saved", result.Status);
        Assert.Empty(world.CreatedIssues);
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task A_report_that_cannot_be_made_ends_the_run_without_opening_anything()
    {
        var world = Improving();
        world.ImprovementReportFailure = new Temporalio.Exceptions.ApplicationFailureException("the week is not valid", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("improvement_failed", result.Status);
        Assert.Empty(world.CreatedIssues);
    }

    [SkippableFact]
    public async Task A_ticket_that_cannot_be_opened_ends_the_run_before_any_agent_is_asked()
    {
        var world = Improving();
        world.IssueFailure = new Temporalio.Exceptions.ApplicationFailureException("Forgejo rejected the ticket", nonRetryable: true);

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("improvement_failed", result.Status);
        Assert.Empty(world.AgentCalls);
    }

    [SkippableFact]
    public async Task An_improver_that_is_not_signed_in_blocks_the_run()
    {
        var world = new FakeWorld().Agent("improver", AgentReply.NotSignedIn);

        var result = await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        Assert.Equal("blocked", result.Status);
    }

    [SkippableFact]
    public async Task The_run_is_in_the_usage_log_as_an_improvement_so_the_next_report_can_see_its_cost()
    {
        var world = Improving();

        await world.RunAsync(fixture, context => context.RunImprovementAsync(ScenarioContext.Improvement()));

        var entry = Assert.Single(world.Snapshot(world.UsageLogs));
        Assert.Equal(("improvement", "improvement_posted"), (entry.Kind, entry.Status));
        Assert.Equal(91, entry.Number); // the report's ticket
    }

    // ---- the weekly start ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task The_service_starts_one_run_for_the_last_completed_week_and_never_a_second_for_the_same_week()
    {
        var world = new FakeWorld();

        var (first, second) = await world.RunAsync(fixture, async context =>
        {
            var settings = new WorkerSettings { TemporalTaskQueue = context.Queue };
            var service = new ImprovementService(context.Client, settings, NullLogger<ImprovementService>.Instance, () => new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
            var started = await service.StartForLastWeekAsync();
            var handle = context.Client.GetWorkflowHandle("improvement-2026-w46");
            await handle.GetResultAsync<WorkResult>();
            return (started, await service.StartForLastWeekAsync());
        });

        Assert.True(first);
        Assert.False(second);
        var request = Assert.Single(world.ImprovementRequests);
        Assert.Equal(("2026-W46", "2026-11-09", "2026-11-16"), (request.Week, request.From, request.To));
    }

    [SkippableFact]
    public async Task A_week_whose_report_failed_is_not_started_again_by_itself_so_a_broken_week_cannot_loop_every_hour()
    {
        var world = new FakeWorld { ImprovementReportFailure = new Temporalio.Exceptions.ApplicationFailureException("disk full", nonRetryable: true) };

        var (status, second) = await world.RunAsync(fixture, async context =>
        {
            var service = new ImprovementService(context.Client, new WorkerSettings { TemporalTaskQueue = context.Queue }, NullLogger<ImprovementService>.Instance, () => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
            await service.StartForLastWeekAsync();
            var failed = await context.Client.GetWorkflowHandle("improvement-2026-w47").GetResultAsync<WorkResult>();
            return (failed.Status, await service.StartForLastWeekAsync());
        });

        // The run ended with a status (visible in Temporal), not with a failure of the workflow, so the service does not start it again: a person can, once the cause is gone.
        Assert.Equal("improvement_failed", status);
        Assert.False(second);
    }
}
