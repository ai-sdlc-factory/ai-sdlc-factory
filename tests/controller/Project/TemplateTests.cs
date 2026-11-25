using System.ComponentModel;
using System.Diagnostics;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>Every starter project under templates/ must carry settings the factory's own parser accepts, and the files an agent is told about.</summary>
public sealed class TemplateTests
{
    /// <summary>The templates folder, found by walking up from the test binaries; null where it was not copied (then these tests have nothing to check).</summary>
    private static string? Templates()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "templates");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return candidate;
            }
        }
        return null;
    }

    public static IEnumerable<object[]> Names() =>
        // "examples" holds loose files to copy, not a starter project.
        Templates() is { } root ? Directory.GetDirectories(root).Where(path => Path.GetFileName(path) != "examples").Select(path => new object[] { Path.GetFileName(path) }) : [];

    [Fact]
    public void There_are_templates_to_check_wherever_the_folder_was_copied()
    {
        if (Templates() is null)
        {
            return;
        }
        Assert.NotEmpty(Names());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_settings_are_accepted_by_the_factorys_parser(string name)
    {
        var json = File.ReadAllText(Path.Combine(Templates()!, name, ".factory", "project.json"));

        Assert.True(ProjectConfigParser.TryParse(json, out var config, out var error), error);
        Assert.NotEmpty(config!.Gates);
        Assert.True(config.Record);
        Assert.Contains("security", config.Reviewers);
        Assert.NotEmpty(config.Rules!);
        Assert.NotNull(config.Limits);
        Assert.NotEmpty(config.ProtectedPaths!);
        Assert.Empty(config.Warnings!); // the templates use every setting, so a key the parser reads but the known-key list lacks shows up here
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_end_to_end_setup_names_tests_that_exist(string name)
    {
        var json = File.ReadAllText(Path.Combine(Templates()!, name, ".factory", "project.json"));
        ProjectConfigParser.TryParse(json, out var config, out _);

        Assert.NotNull(config!.Verify);
        Assert.True(Directory.Exists(Path.Combine(Templates()!, name, config.Verify!.Tests)), $"{name}: verify.tests {config.Verify.Tests} does not exist");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_gate_that_runs_in_a_folder_names_a_folder_that_exists(string name)
    {
        var json = File.ReadAllText(Path.Combine(Templates()!, name, ".factory", "project.json"));
        ProjectConfigParser.TryParse(json, out var config, out _);

        foreach (var gate in config!.Gates.Where(gate => gate.WorkingDirectory is not null))
        {
            Assert.True(Directory.Exists(Path.Combine(Templates()!, name, gate.WorkingDirectory!)), $"{name}: gate {gate.Name} runs in {gate.WorkingDirectory}, which does not exist");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_files_an_agent_and_a_new_user_need_are_there(string name)
    {
        var root = Path.Combine(Templates()!, name);

        foreach (var file in new[] { "README.md", "AGENTS.md", ".gitignore", ".factory/project.json" })
        {
            Assert.True(File.Exists(Path.Combine(root, file)), $"{name} has no {file}");
        }
        Assert.Contains("## Do not touch", File.ReadAllText(Path.Combine(root, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(root, ".factory", "Dockerfile")), "a template must not switch a project image on by itself; ship it as Dockerfile.example");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_project_instructions_are_read_by_the_factory_and_stay_within_their_limit(string name)
    {
        var text = File.ReadAllText(Path.Combine(Templates()!, name, ".factory", "instructions.md"));
        var sectioned = text.Split('\n').Where(line => line.StartsWith("## ", StringComparison.Ordinal)).Select(line => line[3..].Trim()).ToList();

        Assert.True(text.Length <= ProjectInstructions.MaxBytes, $"{name}: instructions.md is over the limit and would be cut");
        Assert.NotEmpty(sectioned);
        foreach (var role in sectioned)
        {
            Assert.Contains(role, AgentRoles.All);
            Assert.NotNull(ProjectInstructions.For(text, role));
        }
        Assert.Contains("never relax", ProjectInstructions.Block(text, sectioned[0]));
    }

    private static readonly string[] BuildOutput = ["bin", "obj", "node_modules", "dist"];

    /// <summary>The paths among these that are inside a folder a build makes (<c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>dist</c>), as a name of a whole folder and not part of a file's name.</summary>
    internal static IReadOnlyList<string> BuildOutputIn(IEnumerable<string> paths) =>
        paths.Where(path => path.Replace('\\', '/').Split('/').SkipLast(1).Any(BuildOutput.Contains)).ToList();

    /// <summary>The files of a folder that git has checked in, as paths inside it; null where git cannot say (it is not installed, or the folder is not in a git checkout, as in the test image).</summary>
    private static IReadOnlyList<string>? CheckedInFiles(string folder)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "ls-files", "-z", "--", "." })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000) || process.ExitCode != 0)
            {
                return null;
            }
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Nothing_that_a_build_makes_is_checked_in(string name)
    {
        var root = Path.Combine(Templates()!, name);

        // What counts is what is checked in: a build on a developer's machine leaves bin and obj folders next to the sources, which git ignores and which are no problem. Where git cannot say
        // (the test image has no checkout, and keeps those folders out of what it copies), the folders themselves are looked for.
        if (CheckedInFiles(root) is { } checkedIn)
        {
            Assert.Empty(BuildOutputIn(checkedIn));
            return;
        }
        foreach (var folder in BuildOutput)
        {
            Assert.Empty(Directory.GetDirectories(root, folder, SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void A_folder_a_build_makes_is_told_from_a_file_that_only_has_the_same_letters()
    {
        Assert.Equal(["src/Api/bin/Debug/Api.dll", "web/node_modules/x/index.js", "obj/project.assets.json"],
            BuildOutputIn(["src/Api/bin/Debug/Api.dll", "src/Api/Program.cs", "docs/binary.md", "web/node_modules/x/index.js", "web/src/distance.ts", "obj/project.assets.json", "src/bin"]));
    }
}
