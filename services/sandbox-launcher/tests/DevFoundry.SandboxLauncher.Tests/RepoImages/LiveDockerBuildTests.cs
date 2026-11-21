namespace DevFoundry.SandboxLauncher.Tests;

/// <summary>
/// Runs the real build path against a real Docker daemon. Skipped (it passes without doing anything) unless DEV_FOUNDRY_LIVE_DOCKER=1 and the
/// Docker socket is available, because it needs the base sandbox image to exist. From the repository root:
/// docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -e DEV_FOUNDRY_LIVE_DOCKER=1 -e TEST_FILTER='Category=Live' t
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveDockerBuildTests
{
    private const string Base = "dev-foundry/sandbox-dotnet:local";

    [Fact]
    public async Task A_dockerfile_is_built_labelled_reused_and_runs_as_an_image_the_policy_accepts()
    {
        if (Environment.GetEnvironmentVariable("DEV_FOUNDRY_LIVE_DOCKER") != "1")
        {
            return;
        }
        var docker = DockerClient.FromSocket("/var/run/docker.sock");
        var settings = Support.Settings(allowRepoImages: true);
        var builder = new RepoImageBuilder(docker, settings);
        var marker = Guid.NewGuid().ToString("N");
        var request = new SandboxBuildRequest(
            $"FROM {Base}\nCOPY hello.txt /opt/hello.txt\nRUN echo build-{marker} > /opt/built.txt\nLABEL marker={marker}",
            new Dictionary<string, string> { ["hello.txt"] = Convert.ToBase64String("hello\n"u8.ToArray()) });
        string? tag = null;
        try
        {
            var first = await builder.BuildAsync(request, () => { }, CancellationToken.None);
            tag = RepoImageNames.TagFor(first.Image);

            Assert.True(first.Built);
            Assert.Contains("build-" + marker, first.LogTail);
            var image = await docker.InspectImageAsync(tag, CancellationToken.None);
            Assert.NotNull(image);
            Assert.Equal("true", image!.Labels[RepoImageNames.Label]);
            Assert.Equal(marker, image.Labels["marker"]);

            var second = await builder.BuildAsync(request, () => { }, CancellationToken.None);
            Assert.False(second.Built);
            Assert.Equal(first.Image, second.Image);

            // The policy resolves the alias to the tag exactly as the activity does.
            var run = SandboxPolicy.Validate(Support.Request(image: first.Image), settings, _ => true, alias => alias == first.Image ? tag : null);
            Assert.Equal(tag, run.Image);
        }
        finally
        {
            if (tag is not null)
            {
                await docker.RemoveImageAsync(tag, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task A_failing_run_step_reports_the_build_output_and_leaves_no_image()
    {
        if (Environment.GetEnvironmentVariable("DEV_FOUNDRY_LIVE_DOCKER") != "1")
        {
            return;
        }
        var docker = DockerClient.FromSocket("/var/run/docker.sock");
        var builder = new RepoImageBuilder(docker, Support.Settings(allowRepoImages: true));
        var marker = Guid.NewGuid().ToString("N");

        var error = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => builder.BuildAsync(
            new SandboxBuildRequest($"FROM {Base}\nRUN echo about-to-fail-{marker} && exit 7"), () => { }, CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("about-to-fail-" + marker, error.Message);
        Assert.Contains("returned a non-zero code: 7", error.Message);
    }
}
