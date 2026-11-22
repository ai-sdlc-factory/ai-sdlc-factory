namespace AiSdlcFactory.Controller;

/// <summary>
/// A pull request that changes only the user interface, in a project that has <c>ui</c> paths but no <c>ui</c> suite in <c>verify.suites</c>: the tester can then only write API tests,
/// which never touch what changed, and a report of "no problems found" would say less than it seems to. Decided by comparing paths, so no model is involved and it costs nothing.
/// </summary>
public static class QaUiGap
{
    /// <summary>
    /// The sentence to say, or null when there is nothing to say: the changed files are not known or not all of them (a list that was cut proves nothing), some file is not a UI file,
    /// the project has a <c>ui</c> suite, or a person chose the suite to run.
    /// </summary>
    public static string? Describe(PullChanges? changes, UiConfig? ui, VerifyConfig verify, string? requestedSuite)
    {
        if (requestedSuite is not null || ui is null || changes is null || changes.Files.Count == 0 || changes.TotalFiles > changes.Files.Count
            || verify.Suites.Any(suite => suite.Name == VerifyConfig.Ui)
            || changes.Files.Any(file => !Globs.IsMatchAny(file.Path, ui.Paths)))
        {
            return null;
        }
        var count = changes.Files.Count;
        return $"{(count == 1 ? "The one file" : $"All {count} files")} this pull request changes {(count == 1 ? "is a UI file" : "are UI files")} (`ui.paths`), " +
            "and this project has no `ui` suite in `verify.suites`, so the tests can only exercise the API and not what changed. " +
            "A report of \"no problems found\" would not say the UI was tested. To test the UI, add a `ui` suite.";
    }
}
