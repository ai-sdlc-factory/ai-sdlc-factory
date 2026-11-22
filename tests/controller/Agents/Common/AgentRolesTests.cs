namespace AiSdlcFactory.Controller.Tests;

public sealed class AgentRolesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "roles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>The repository's agents/agents folder, found by walking up from the test binaries; null where it was not copied.</summary>
    private static string? RepositoryRoles()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            var candidate = Path.Combine(parent.FullName, "agents", "agents");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(parent.FullName, "src")))
            {
                return candidate;
            }
        }
        return null;
    }

    [SkippableFact]
    public void The_list_of_roles_is_the_repositorys_role_files()
    {
        var roles = RepositoryRoles();
        Skip.If(roles is null, "agents/ was not copied");

        var files = Directory.GetFiles(roles!, "*.md").Select(file => Path.GetFileNameWithoutExtension(file)!).Order().ToList();

        Assert.Equal(files, AgentRoles.All.Order().ToList());
    }

    [Fact]
    public void Roles_without_a_file_are_listed_and_an_unknown_folder_says_nothing()
    {
        Directory.CreateDirectory(Path.Combine(directory, "agents"));
        foreach (var role in AgentRoles.All.Where(role => role != "planner" && role != "asker"))
        {
            File.WriteAllText(Path.Combine(directory, "agents", role + ".md"), "---\n---\n");
        }

        Assert.Equal(["asker", "planner"], AgentRoles.Missing(directory).Order().ToList());
        Assert.Empty(AgentRoles.Missing(""));
        Assert.Empty(AgentRoles.Missing(null));
        Assert.Equal(AgentRoles.All.Count, AgentRoles.Missing(Path.Combine(directory, "nowhere")).Count);
    }
}
