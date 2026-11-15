namespace DevFoundry.Controller.Tests;

/// <summary>A pull request that changes only the UI, in a project with ui paths but no ui suite, is noticed by comparing paths before a tester run is spent.</summary>
public sealed class QaUiGapTests
{
    private static VerifyConfig Verify(params string[] suites)
    {
        var list = string.Join(",", suites.Select(name => "{\"name\":\"" + name + "\",\"command\":[\"npm\",\"run\",\"e2e:" + name + "\"]}"));
        var json = "{\"image\":\"node\",\"gates\":[{\"name\":\"t\",\"command\":[\"npm\",\"test\"]}],\"verify\":{\"start\":[\"npm\",\"start\"],\"ready\":\"http://127.0.0.1:3000/\",\"tests\":\"e2e\",\"suites\":[" + list + "]}}";
        Assert.True(ProjectConfigParser.TryParse(json, out var config, out var error), error);
        return config!.Verify!;
    }

    private static readonly UiConfig Ui = new(["web/**", "*.css"], UiConfig.DefaultMedium, UiConfig.DefaultLarge);

    private static PullChanges Changes(params string[] paths) =>
        new(paths.Select(path => new ChangedFile(path, "modified", 1, 1)).ToList(), paths.Length, "", false);

    [Fact]
    public void A_change_that_is_all_ui_in_a_project_without_a_ui_suite_is_noticed()
    {
        var text = QaUiGap.Describe(Changes("web/app.tsx", "web/styles.css"), Ui, Verify("api"), null);

        Assert.Contains("All 2 files this pull request changes are UI files (`ui.paths`)", text);
        Assert.Contains("no `ui` suite in `verify.suites`", text);
        Assert.Contains("add a `ui` suite", text);
    }

    [Fact]
    public void One_file_is_said_in_the_singular() =>
        Assert.StartsWith("The one file this pull request changes is a UI file", QaUiGap.Describe(Changes("web/app.tsx"), Ui, Verify("api"), null));

    [Fact]
    public void Nothing_is_said_when_any_file_is_not_a_ui_file() =>
        Assert.Null(QaUiGap.Describe(Changes("web/app.tsx", "api/Handler.cs"), Ui, Verify("api"), null));

    [Fact]
    public void Nothing_is_said_when_the_project_has_a_ui_suite() =>
        Assert.Null(QaUiGap.Describe(Changes("web/app.tsx"), Ui, Verify("api", "ui"), null));

    [Fact]
    public void Nothing_is_said_when_a_person_chose_the_suite() =>
        Assert.Null(QaUiGap.Describe(Changes("web/app.tsx"), Ui, Verify("api"), "api"));

    [Fact]
    public void Nothing_is_said_when_the_project_has_no_ui_paths_or_the_changes_are_unknown_or_empty()
    {
        Assert.Null(QaUiGap.Describe(Changes("web/app.tsx"), null, Verify("api"), null));
        Assert.Null(QaUiGap.Describe(null, Ui, Verify("api"), null));
        Assert.Null(QaUiGap.Describe(Changes(), Ui, Verify("api"), null));
    }

    [Fact]
    public void A_list_of_files_that_was_cut_proves_nothing_so_nothing_is_said()
    {
        var cut = new PullChanges([new ChangedFile("web/app.tsx", "modified", 1, 1)], 400, "", true);

        Assert.Null(QaUiGap.Describe(cut, Ui, Verify("api"), null));
    }

    [Fact]
    public void The_scope_line_is_the_same_wherever_it_is_shown()
    {
        Assert.Equal("**Scope:** this pull request's change.", QaReportFormat.ScopeLine(QaRequest.Focused));
        Assert.Equal("**Scope:** the whole application and this pull request's change.", QaReportFormat.ScopeLine(QaRequest.Full));
        Assert.Contains("**Scope:** the whole application", TicketComments.QaBlocked("The app does not run.", QaRequest.Full));
        Assert.Contains("**Scope:** this pull request's change.", TicketComments.QaBlocked("The app does not run."));
        Assert.Contains("**Scope:**", TicketComments.QaUiGap("All of it is UI.", QaRequest.Focused));
    }
}
