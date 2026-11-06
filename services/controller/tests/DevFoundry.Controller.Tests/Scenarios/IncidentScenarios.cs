using Temporalio.Client;
using Temporalio.Exceptions;

namespace DevFoundry.Controller.Tests.Scenarios;

/// <summary>The incident workflow: advice only. People drive it with /option, /hotfix and /resolved.</summary>
[Collection("temporal")]
[Trait("Category", "Scenario")]
public sealed class IncidentScenarios(TemporalFixture fixture)
{
    private static AgentReply Say(string text) => AgentReply.Says(text);

    private static FeatureRequest Incident() =>
        ScenarioContext.Request(title: "Checkout fails", body: "500s on /checkout since 10:42. Last deploy at 10:40.", labels: ["incident"]) with { Kind = FeatureIds.Incident };

    /// <summary>The three advice roles, answering once each; tests add more answers for repeated looks.</summary>
    private static FakeWorld Advising() => new FakeWorld()
        .Agent("incident-analyst", Say(Answers.IncidentAssessment()))
        .Agent("investigator", Say(Answers.Investigation()))
        .Agent("mitigator", Say(Answers.Mitigation()));

    private static string[] Roles(FakeWorld world) => world.Snapshot(world.AgentCalls).Select(call => call.Role).ToArray();

    private static int Count(FakeWorld world, string text) => world.Snapshot(world.Comments).Count(c => c.Contains(text));

    private static async Task WaitForCountAsync(FakeWorld world, string text, int count)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (Count(world, text) < count)
        {
            Assert.True(DateTime.UtcNow < until, $"'{text}' did not reach {count} comments. Comments:\n" + string.Join("\n---\n", world.Snapshot(world.Comments)));
            await Task.Delay(50);
        }
    }

    private static Task Say_(WorkflowHandle<IncidentWorkflow, FeatureResult> handle, string text, string who = "bob") =>
        handle.SignalAsync(w => w.OnHumanReplyAsync(new HumanReply(who, text)));

    private static string PostmortemPath(int issue = 1) => $"docs/postmortems/{DateTime.UtcNow:yyyy-MM-dd}-incident-{issue}.md";

    private static ChangeReport WroteOnly(string path) => new([new FileChange(path, FileChange.Added)], []);

    [SkippableFact]
    public async Task An_incident_is_assessed_investigated_and_given_options_then_it_waits_for_a_person()
    {
        var world = Advising();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Equal(["incident-analyst", "investigator", "mitigator"], Roles(world));
        Assert.Contains(world.Comments, c => c.Contains("**Incident assessment: severity high**"));
        Assert.Contains(world.Comments, c => c.Contains("**Investigation: likely causes**") && c.Contains("`hello.py`") && c.Contains("`a1b2c3d`"));
        Assert.Contains(world.Comments, c => c.Contains("**Option 1 (recommended): Option 1 title**"));
        Assert.Empty(world.PullRequests); // advice only: nothing was built or published
        Assert.Empty(world.Publishes);
        Assert.Empty(world.Problems);
    }

    [SkippableFact]
    public async Task The_investigator_cannot_write_and_the_postmortem_writer_can()
    {
        var world = Advising().Agent("postmortem-writer", Say(Answers.Postmortem())).Changes(WroteOnly(PostmortemPath()));

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/resolved");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.False(world.Calls("incident-analyst").Single().Writable);
        Assert.False(world.Calls("investigator").Single().Writable);
        Assert.False(world.Calls("mitigator").Single().Writable);
        Assert.True(world.Calls("postmortem-writer").Single().Writable);
        Assert.All(new[] { "incident-analyst", "investigator", "mitigator" }, role => Assert.Equal(SandboxNetworks.Model, world.Calls(role).Single().Network));
    }

    [SkippableFact]
    public async Task A_person_gets_the_checklist_for_an_option_and_then_a_draft_postmortem_as_a_pull_request()
    {
        var world = Advising().Agent("postmortem-writer", Say(Answers.Postmortem())).Changes(WroteOnly(PostmortemPath()));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/option 2");
            await WaitForCountAsync(world, "Checklist for option 2", 1);
            await Say_(handle, "Rolled back at 11:05, errors are gone.\n/resolved");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("postmortem_opened", result.Status);
        Assert.Equal("http://forgejo/pulls/1", result.PullRequestUrl);
        var checklist = Assert.Single(world.Comments, c => c.Contains("Checklist for option 2"));
        Assert.Contains("- [ ] Step A of option 2", checklist);
        Assert.Contains("A person carries these out", checklist);

        var pull = Assert.Single(world.PullRequests);
        Assert.StartsWith("Postmortem: ", pull.Title);
        Assert.Contains("Draft postmortem", pull.Body);
        Assert.Contains("Refs #1", pull.Body);
        Assert.Contains("A person must verify the facts", pull.Body);
        Assert.Contains("Postmortem", Assert.Single(world.Publishes).Message);
        Assert.Contains(world.Comments, c => c.Contains("**Draft postmortem:** http://forgejo/pulls/1") && c.Contains("A person must check the facts"));

        // The postmortem writer is given the record of the ticket, in order, and the one file it may write.
        var prompt = world.Calls("postmortem-writer").Single().Prompt;
        Assert.Contains($"Write exactly one new file: {PostmortemPath()}", prompt);
        Assert.Contains("bob: /option 2", prompt);
        Assert.Contains("factory: Checklist given for option 2.", prompt);
        Assert.Contains("Rolled back at 11:05, errors are gone.", prompt);
        Assert.DoesNotContain(Roles(world), role => role is "developer" or "qa" or "coverage" or "reproducer");
    }

    [SkippableFact]
    public async Task Evidence_the_investigator_cannot_back_up_is_dropped_and_shown_as_a_guess()
    {
        var hypotheses = new object[]
        {
            new { cause = "The retry loop doubles the load.", confidence = "high", evidence = new[] { "hello.py" }, commits = new[] { "a1b2c3d" } },
            new { cause = "A cache is misconfigured.", confidence = "high", evidence = new[] { "ghost.py" }, commits = new[] { "deadbee" } },
        };
        var world = new FakeWorld { ExistingFiles = ["hello.py", "a1b2c3d"] }
            .Agent("incident-analyst", Say(Answers.IncidentAssessment()))
            .Agent("investigator", Say(Answers.Investigation(hypotheses: hypotheses)))
            .Agent("mitigator", Say(Answers.Mitigation()));

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        var comment = Assert.Single(world.Comments, c => c.Contains("**Investigation: likely causes**"));
        Assert.Contains("1. **high confidence:** The retry loop doubles the load.", comment);
        Assert.Contains("2. **low confidence, not backed by anything I could find:** A cache is misconfigured.", comment);
        Assert.DoesNotContain("ghost.py", comment);
        Assert.DoesNotContain("deadbee", comment);
        Assert.Contains("[low, unverified] A cache is misconfigured.", world.Calls("mitigator").Single().Prompt);
    }

    [SkippableFact]
    public async Task The_hotfix_command_opens_one_bug_ticket_that_carries_what_is_known()
    {
        var world = Advising();

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/hotfix");
            await WaitForCountAsync(world, "Opened bug ticket #91", 1);
            await Say_(handle, "/hotfix");
            await WaitForCountAsync(world, "was already opened", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        var issue = Assert.Single(world.CreatedIssues);
        Assert.Equal(["bug"], issue.Labels);
        Assert.StartsWith("Fix for incident #1: Checkout fails", issue.Title);
        Assert.Contains("incident #1", issue.Body);
        Assert.Contains("The new retry loop doubles the load.", issue.Body);
        Assert.Contains("check it before relying on it", issue.Body);
        Assert.Contains("`a1b2c3d`", issue.Body);
        Assert.Contains(world.Comments, c => c.Contains("http://forgejo/issues/91") && c.Contains("A person still has to review and merge"));
    }

    [SkippableFact]
    public async Task A_bug_ticket_that_cannot_be_opened_is_reported_not_hidden()
    {
        var world = Advising();
        world.IssueFailure = new ApplicationFailureException("The repository has no \"bug\" label, so the ticket could not be opened.", nonRetryable: true);

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/hotfix");
            await WaitForCountAsync(world, "I could not open the bug ticket", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Contains(world.Comments, c => c.Contains("has no \"bug\" label"));
        Assert.Empty(world.CreatedIssues);
    }

    [SkippableFact]
    public async Task An_option_that_does_not_exist_is_answered_and_nothing_else_happens()
    {
        var world = Advising();

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/option 9");
            await WaitForCountAsync(world, "There is no option 9. The options are 1 to 2.", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal(0, Count(world, "Checklist for option"));
        Assert.Equal(["incident-analyst", "investigator", "mitigator"], Roles(world)); // no new look at the incident
    }

    [SkippableFact]
    public async Task A_comment_with_new_information_makes_the_investigator_and_the_mitigator_look_again()
    {
        var world = new FakeWorld()
            .Agent("incident-analyst", Say(Answers.IncidentAssessment()))
            .Agent("investigator", Say(Answers.Investigation()), Say(Answers.Investigation()))
            .Agent("mitigator", Say(Answers.Mitigation()), Say(Answers.Mitigation(options: 3)));

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "The errors only hit EU users.");
            await WaitForCountAsync(world, "Options to reduce the harm", 2);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal(["incident-analyst", "investigator", "mitigator", "investigator", "mitigator"], Roles(world));
        Assert.Contains("The errors only hit EU users.", world.Calls("investigator").ElementAt(1).Prompt);
        Assert.Contains("The errors only hit EU users.", world.Calls("mitigator").ElementAt(1).Prompt);
        Assert.DoesNotContain("EU users", world.Calls("investigator").First().Prompt);
    }

    [SkippableFact]
    public async Task The_looks_are_limited_and_the_person_is_told_what_to_do_instead()
    {
        var world = new FakeWorld()
            .Agent("incident-analyst", Say(Answers.IncidentAssessment()))
            .Agent("investigator", Enumerable.Range(0, 4).Select(_ => Say(Answers.Investigation())).ToArray())
            .Agent("mitigator", Enumerable.Range(0, 4).Select(_ => Say(Answers.Mitigation())).ToArray());

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            for (var round = 1; round <= 4; round++)
            {
                await WaitForCountAsync(world, "Options to reduce the harm", round);
                await Say_(handle, $"More news {round}.");
            }
            await WaitForCountAsync(world, "I have already looked again several times", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal(4, world.Calls("investigator").Count()); // the first look plus three more
        Assert.Equal(4, world.Calls("mitigator").Count());
        Assert.Contains(world.Comments, c => c.Contains("Use `/option N`, `/hotfix` or `/resolved`"));
    }

    [SkippableFact]
    public async Task A_report_the_analyst_says_is_not_an_incident_stops_at_once()
    {
        var world = new FakeWorld().Agent("incident-analyst", Say(Answers.IncidentAssessment("reject")));

        var result = await world.RunAsync(fixture, context => context.RunToEndAsync(Incident()));

        Assert.Equal("rejected", result.Status);
        Assert.Equal(["incident-analyst"], Roles(world));
    }

    [SkippableFact]
    public async Task An_unclear_ticket_gets_questions_and_continues_with_the_answer()
    {
        var world = new FakeWorld()
            .Agent("incident-analyst", Say(Answers.IncidentAssessment("needs_clarification")), Say(Answers.IncidentAssessment()))
            .Agent("investigator", Say(Answers.Investigation()))
            .Agent("mitigator", Say(Answers.Mitigation()));

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await world.WaitForCommentAsync(c => c.Contains("I need more information") && c.Contains("Which environment?"));
            await Say_(handle, "Production, EU region.");
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Contains("Production, EU region.", world.Calls("incident-analyst").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_postmortem_that_writes_the_wrong_file_is_sent_back_once()
    {
        var world = Advising()
            .Agent("postmortem-writer", Say(Answers.Postmortem()), Say(Answers.Postmortem()))
            .Changes(WroteOnly("docs/notes.md"), WroteOnly(PostmortemPath()));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/resolved");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("postmortem_opened", result.Status);
        Assert.Equal(2, world.Calls("postmortem-writer").Count());
        Assert.Contains("Write exactly one new file", world.Calls("postmortem-writer").ElementAt(1).Prompt);
        Assert.Contains("added docs/notes.md", world.Calls("postmortem-writer").ElementAt(1).Prompt);
    }

    [SkippableFact]
    public async Task A_postmortem_that_is_wrong_twice_publishes_nothing()
    {
        var world = Advising()
            .Agent("postmortem-writer", Say(Answers.Postmortem()), Say(Answers.Postmortem()))
            .Changes(WroteOnly("docs/notes.md"), WroteOnly("docs/other.md"));

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/resolved");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("postmortem_failed", result.Status);
        Assert.Empty(world.PullRequests);
        Assert.Empty(world.Publishes);
        Assert.Contains(world.Comments, c => c.Contains("nothing was published"));
    }

    [SkippableFact]
    public async Task Everyone_sees_why_the_run_ended_when_nobody_answers_for_a_week()
    {
        // A week-long timer is not waited out here; closing the ticket is the way a person ends the wait.
        var world = Advising();

        var result = await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await handle.SignalAsync(w => w.OnTicketClosedAsync());
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal("closed", result.Status);
        Assert.Equal(0, Count(world, "was not used")); // a closed ticket needs no notice about unread comments
    }

    [SkippableFact]
    public async Task The_incident_is_recorded_when_the_project_keeps_a_record()
    {
        var world = Advising().Agent("postmortem-writer", Say(Answers.Postmortem())).Changes(WroteOnly(PostmortemPath()));
        world.ProjectConfig = """{"image":"python","record":true,"gates":[{"name":"test","command":["python","-m","pytest","-q"]}]}""";

        await world.RunAsync(fixture, async context =>
        {
            var handle = await context.StartIncidentAsync(Incident());
            await WaitForCountAsync(world, "Options to reduce the harm", 1);
            await Say_(handle, "/resolved");
            return await ScenarioContext.ResultAsync(handle);
        });

        Assert.Equal(["incident-analyst", "investigator", "mitigator", "postmortem-writer"], world.Records.Where(r => r.Kind == "agent").Select(r => r.Slug));
        Assert.NotNull(Assert.Single(world.Publishes).Record);
    }
}
