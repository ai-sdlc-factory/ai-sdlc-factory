using Temporalio.Exceptions;

namespace AiSdlcFactory.Controller.Tests;

public sealed class RepoImageSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ri-" + Guid.NewGuid().ToString("N"));
    private const string Name = "feature-acme-shop-1";

    public RepoImageSourceTests() => Directory.CreateDirectory($"{root}/.base/{Name}/.factory");

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Factory => $"{root}/.base/{Name}/.factory";
    private GitWorkspace Workspace() => new(new WorkerSettings { Hosts = TestHosts.One(), WorkspaceRoot = root });

    [Fact]
    public void A_repository_without_a_dockerfile_has_no_image_definition()
    {
        Assert.Null(Workspace().ReadProjectImage(Name));
    }

    [Fact]
    public void The_dockerfile_and_the_context_next_to_it_are_read()
    {
        File.WriteAllText($"{Factory}/Dockerfile", "FROM x\nRUN true\n");
        Directory.CreateDirectory($"{Factory}/docker/sub");
        File.WriteAllText($"{Factory}/docker/setup.sh", "#!/bin/sh\n");
        File.WriteAllText($"{Factory}/docker/sub/conf.ini", "a=1\n");

        var source = Workspace().ReadProjectImage(Name)!;

        Assert.Equal("FROM x\nRUN true\n", source.Dockerfile);
        Assert.Equal(["setup.sh", "sub/conf.ini"], source.Files.Keys);
        Assert.Equal("#!/bin/sh\n", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(source.Files["setup.sh"])));
    }

    [Fact]
    public void A_dockerfile_that_is_a_symbolic_link_is_ignored()
    {
        File.WriteAllText($"{root}/outside", "FROM x\n");
        File.CreateSymbolicLink($"{Factory}/Dockerfile", $"{root}/outside");

        Assert.Null(Workspace().ReadProjectImage(Name));
    }

    [Fact]
    public void A_context_file_that_is_a_symbolic_link_stops_the_run()
    {
        File.WriteAllText($"{Factory}/Dockerfile", "FROM x\n");
        Directory.CreateDirectory($"{Factory}/docker");
        File.WriteAllText($"{root}/secret", "s");
        File.CreateSymbolicLink($"{Factory}/docker/link", $"{root}/secret");

        var error = Assert.Throws<ApplicationFailureException>(() => Workspace().ReadProjectImage(Name));

        Assert.Contains("symbolic link", error.Message);
        Assert.True(error.NonRetryable);
    }

    [Fact]
    public void A_context_that_is_too_big_stops_the_run()
    {
        File.WriteAllText($"{Factory}/Dockerfile", "FROM x\n");
        Directory.CreateDirectory($"{Factory}/docker");
        File.WriteAllBytes($"{Factory}/docker/big.bin", new byte[(int)GitWorkspace.MaxImageContextBytes + 1]);

        Assert.Contains("more than 50 files or more than 1 MB", Assert.Throws<ApplicationFailureException>(() => Workspace().ReadProjectImage(Name)).Message);
    }

    [Fact]
    public void A_dockerfile_over_64_kb_stops_the_run()
    {
        File.WriteAllText($"{Factory}/Dockerfile", "# " + new string('x', 70_000));

        Assert.Contains("larger than 64 KB", Assert.Throws<ApplicationFailureException>(() => Workspace().ReadProjectImage(Name)).Message);
    }
}
