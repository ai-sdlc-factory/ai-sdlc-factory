using Microsoft.Extensions.Configuration;
using System.Formats.Tar;
using System.Text;
using Temporalio.Exceptions;

namespace AiSdlcFactory.SandboxLauncher.Tests;

public sealed class RepoImagePolicyTests
{
    private static readonly string[] Bases = ["ai-sdlc-factory/sandbox-dotnet:local", "ai-sdlc-factory/sandbox-node:local"];

    private static SandboxBuildRequest Request(string dockerfile, Dictionary<string, string>? files = null) => new(dockerfile, files);

    private static string Rejected(SandboxBuildRequest request) =>
        Assert.Throws<ApplicationFailureException>(() => RepoImagePolicy.Validate(request, Bases)).Message;

    [Fact]
    public void A_dockerfile_from_a_factory_image_with_allowed_instructions_passes()
    {
        var build = RepoImagePolicy.Validate(Request("""
            # redis for the integration tests
            FROM ai-sdlc-factory/sandbox-dotnet:local
            ENV DEBIAN_FRONTEND=noninteractive
            RUN apt-get update \
                && apt-get install -y --no-install-recommends redis-server \
                && rm -rf /var/lib/apt/lists/*
            COPY --chmod=755 setup.sh /usr/local/bin/setup.sh
            LABEL purpose=tests
            """, new() { ["setup.sh"] = Convert.ToBase64String("#!/bin/sh\n"u8.ToArray()) }), Bases);

        Assert.Equal("ai-sdlc-factory/sandbox-dotnet:local", build.BaseImage);
        Assert.Equal("setup.sh", Assert.Single(build.Files).Path);
    }

    [Theory]
    [InlineData("RUN echo hi")]
    [InlineData("FROM ubuntu:24.04")]
    [InlineData("FROM ai-sdlc-factory/sandbox-dotnet:local AS build")]
    [InlineData("FROM --platform=linux/amd64 ai-sdlc-factory/sandbox-dotnet:local")]
    [InlineData("ARG BASE=ai-sdlc-factory/sandbox-dotnet:local\nFROM $BASE")]
    public void The_first_instruction_must_be_exactly_from_one_of_the_factorys_images(string dockerfile) =>
        Assert.Contains("first instruction", Rejected(Request(dockerfile)));

    [Theory]
    [InlineData("ENTRYPOINT [\"sh\"]")]
    [InlineData("CMD [\"sh\"]")]
    [InlineData("USER root")]
    [InlineData("WORKDIR /x")]
    [InlineData("VOLUME /data")]
    [InlineData("HEALTHCHECK CMD true")]
    [InlineData("SHELL [\"/bin/bash\"]")]
    [InlineData("ONBUILD RUN true")]
    [InlineData("EXPOSE 80")]
    [InlineData("FROM ai-sdlc-factory/sandbox-node:local")]
    public void Instructions_that_change_how_the_container_starts_are_not_allowed(string instruction) =>
        Assert.Contains("not allowed", Rejected(Request($"FROM ai-sdlc-factory/sandbox-dotnet:local\n{instruction}")));

    [Fact]
    public void An_instruction_hidden_behind_a_line_continuation_is_still_one_instruction()
    {
        // The words after the backslash belong to the RUN, not a new ENTRYPOINT instruction.
        RepoImagePolicy.Validate(Request("FROM ai-sdlc-factory/sandbox-dotnet:local\nRUN echo a \\\n ENTRYPOINT b"), Bases);
    }

    [Theory]
    [InlineData("COPY --from=other /a /b", "--chown")]
    [InlineData("ADD https://example.com/x.sh /x.sh", "fetch a URL")]
    [InlineData("ADD git@github.com:a/b.git /b", "fetch a URL")]
    [InlineData("RUN --mount=type=secret,id=x true", "flags")]
    [InlineData("RUN --network=host true", "flags")]
    public void Flags_that_reach_outside_the_context_are_refused(string instruction, string mentions) =>
        Assert.Contains(mentions, Rejected(Request($"FROM ai-sdlc-factory/sandbox-dotnet:local\n{instruction}")));

    [Theory]
    [InlineData("# syntax=docker/dockerfile:1\nFROM ai-sdlc-factory/sandbox-dotnet:local")]
    [InlineData("FROM ai-sdlc-factory/sandbox-dotnet:local\n# escape=`")]
    public void Parser_directives_are_refused(string dockerfile) =>
        Assert.Contains("parser directives", Rejected(Request(dockerfile)));

    [Theory]
    [InlineData("../x")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../b")]
    [InlineData("Dockerfile")]
    [InlineData("a b")]
    [InlineData("dir/")]
    public void Context_files_need_plain_relative_paths(string path) =>
        Assert.Contains("unusable path", Rejected(Request("FROM ai-sdlc-factory/sandbox-dotnet:local", new() { [path] = "" })));

    [Fact]
    public void The_context_is_limited_in_files_and_size_and_must_be_base64()
    {
        var tooMany = Enumerable.Range(1, 51).ToDictionary(i => $"f{i}", _ => "");
        Assert.Contains("at most", Rejected(Request("FROM ai-sdlc-factory/sandbox-dotnet:local", tooMany)));
        var big = new Dictionary<string, string> { ["big"] = Convert.ToBase64String(new byte[1024 * 1024 + 1]) };
        Assert.Contains("at most", Rejected(Request("FROM ai-sdlc-factory/sandbox-dotnet:local", big)));
        Assert.Contains("base64", Rejected(Request("FROM ai-sdlc-factory/sandbox-dotnet:local", new() { ["a"] = "***" })));
    }

    [Fact]
    public void An_empty_or_huge_dockerfile_is_refused()
    {
        Assert.Contains("1 byte", Rejected(Request("")));
        Assert.Contains("1 byte", Rejected(Request("FROM ai-sdlc-factory/sandbox-dotnet:local\n#" + new string('x', 70_000))));
    }

    [Fact]
    public void The_image_name_depends_on_the_content_and_the_base_image()
    {
        var a = RepoImagePolicy.Validate(Request("FROM ai-sdlc-factory/sandbox-dotnet:local\nRUN echo a"), Bases);
        var b = RepoImagePolicy.Validate(Request("FROM ai-sdlc-factory/sandbox-dotnet:local\nRUN echo b"), Bases);

        var same = RepoImagePolicy.Hash("sha256:1", a);
        Assert.Equal(same, RepoImagePolicy.Hash("sha256:1", RepoImagePolicy.Validate(Request("FROM ai-sdlc-factory/sandbox-dotnet:local\nRUN echo a"), Bases)));
        Assert.NotEqual(same, RepoImagePolicy.Hash("sha256:1", b));
        Assert.NotEqual(same, RepoImagePolicy.Hash("sha256:2", a)); // a rebuilt base image rebuilds the project's image
        Assert.Matches("^[0-9a-f]{12}$", same);
    }

    [Theory]
    [InlineData("repo-0123456789ab", true)]
    [InlineData("repo-0123456789AB", false)]
    [InlineData("repo-0123456789a", false)]
    [InlineData("dotnet", false)]
    [InlineData("repo-0123456789ab/../x", false)]
    [InlineData(null, false)]
    public void Only_content_derived_aliases_are_repository_images(string? alias, bool expected)
    {
        Assert.Equal(expected, RepoImageNames.IsAlias(alias));
        if (expected)
        {
            Assert.Equal("ai-sdlc-factory/repo:0123456789ab", RepoImageNames.TagFor(alias!));
        }
    }
}

public sealed class RepoImageBuilderTests
{
    private sealed class Docker : IDockerClient
    {
        public Dictionary<string, DockerImage> Images { get; } = new() { ["ai-sdlc-factory/sandbox-dotnet:local"] = new("sha256:base", 100, new Dictionary<string, string>()) };
        public List<string> Built { get; } = [];
        public List<string> RemovedImages { get; } = [];
        public Func<Stream, string>? OnBuild { get; set; }
        public Exception? BuildFailure { get; set; }
        public long SizeAfterBuild { get; set; } = 500;
        public string[] Lines { get; set; } = ["Step 1/2 : FROM x\n", " ---> abc\n", "Step 2/2 : RUN echo hi\n", "hi\n"];
        public Dictionary<string, string>? LastLabels { get; private set; }
        public DockerBuildLimits? LastLimits { get; private set; }
        public string? ContextDockerfile { get; private set; }
        public List<string> ContextFiles { get; } = [];

        public Task<DockerImage?> InspectImageAsync(string name, CancellationToken ct) => Task.FromResult(Images.TryGetValue(name, out var image) ? image : null);

        public Task BuildImageAsync(Stream context, string tag, DockerBuildLimits limits, IReadOnlyDictionary<string, string> labels, Action<string> onLine, CancellationToken ct)
        {
            Built.Add(tag);
            LastLimits = limits;
            LastLabels = labels.ToDictionary(p => p.Key, p => p.Value);
            using (var reader = new TarReader(context, leaveOpen: true))
            {
                while (reader.GetNextEntry() is { } entry)
                {
                    var text = new StreamReader(entry.DataStream!).ReadToEnd();
                    if (entry.Name == "Dockerfile") { ContextDockerfile = text; } else { ContextFiles.Add(entry.Name); }
                }
            }
            foreach (var line in Lines) { onLine(line); }
            if (BuildFailure is not null) { throw BuildFailure; }
            Images[tag] = new DockerImage("sha256:new", SizeAfterBuild, labels.ToDictionary(p => p.Key, p => p.Value));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DockerImageListing>> ListRepoImagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DockerImageListing>>([]);
        public Task<bool> RemoveUnusedImageAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task RemoveImageAsync(string name, CancellationToken ct) { RemovedImages.Add(name); Images.Remove(name); return Task.CompletedTask; }

        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> NetworkExistsAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task EnsureNetworkAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> VolumeExistsAsync(string name, CancellationToken ct) => Task.FromResult(true);
        public Task<string> CreateAsync(CreateContainerBody body, CancellationToken ct) => throw new NotSupportedException();
        public Task StartAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> WaitAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<DockerLogs> LogsAsync(string id, int maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSandboxIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private const string Dockerfile = "FROM ai-sdlc-factory/sandbox-dotnet:local\nRUN echo hi";
    private static SandboxBuildRequest Request() => new(Dockerfile, new Dictionary<string, string> { ["setup.sh"] = Convert.ToBase64String("#!/bin/sh\n"u8.ToArray()) });
    private static RepoImageBuilder Builder(Docker docker, bool allow = true) => new(docker, Support.Settings(allowRepoImages: allow));

    [Fact]
    public async Task Nothing_is_built_unless_the_operator_switched_repository_images_on()
    {
        var docker = new Docker();

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => Builder(docker, allow: false).BuildAsync(Request(), () => { }, CancellationToken.None));

        Assert.Contains("switched off", error.Message);
        Assert.True(error.NonRetryable);
        Assert.Empty(docker.Built);
    }

    [Fact]
    public async Task A_new_image_is_built_with_limits_a_label_and_only_the_context_we_were_given()
    {
        var docker = new Docker();

        var result = await Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None);

        Assert.True(result.Built);
        Assert.Matches("^repo-[0-9a-f]{12}$", result.Image);
        Assert.Equal(RepoImageNames.TagFor(result.Image), Assert.Single(docker.Built));
        Assert.Equal(Dockerfile, docker.ContextDockerfile);
        Assert.Equal(["setup.sh"], docker.ContextFiles);
        Assert.Equal("true", docker.LastLabels![RepoImageNames.Label]);
        Assert.Equal(4L * 1024 * 1024 * 1024, docker.LastLimits!.MemoryBytes);
        Assert.Equal(500, result.SizeBytes);
    }

    [Fact]
    public async Task The_same_content_is_not_built_twice()
    {
        var docker = new Docker();
        var first = await Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None);

        var second = await Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None);

        Assert.True(first.Built);
        Assert.False(second.Built);
        Assert.Equal(first.Image, second.Image);
        Assert.Single(docker.Built);
    }

    [Fact]
    public async Task A_rebuilt_base_image_makes_a_new_project_image()
    {
        var docker = new Docker();
        var first = await Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None);
        docker.Images["ai-sdlc-factory/sandbox-dotnet:local"] = new("sha256:rebuilt", 100, new Dictionary<string, string>());

        var second = await Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None);

        Assert.NotEqual(first.Image, second.Image);
        Assert.Equal(2, docker.Built.Count);
    }

    [Fact]
    public async Task A_base_image_that_is_not_built_is_refused_with_a_clear_message()
    {
        var docker = new Docker();
        docker.Images.Clear();

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None));

        Assert.Contains("has not been built", error.Message);
    }

    [Fact]
    public async Task A_failed_build_reports_the_end_of_the_log_and_is_not_retried()
    {
        var docker = new Docker { BuildFailure = new DockerException(System.Net.HttpStatusCode.InternalServerError, "The command '/bin/sh -c apt-get install nope' returned a non-zero code: 100") };

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None));

        Assert.True(error.NonRetryable);
        Assert.Contains("returned a non-zero code: 100", error.Message);
        Assert.Contains("RUN echo hi", error.Message);
        Assert.DoesNotContain("--->", error.Message);
    }

    [Fact]
    public async Task An_image_over_the_size_limit_is_removed()
    {
        var docker = new Docker { SizeAfterBuild = 7L * 1024 * 1024 * 1024 };

        var error = await Assert.ThrowsAsync<ApplicationFailureException>(() => Builder(docker).BuildAsync(Request(), () => { }, CancellationToken.None));

        Assert.Contains("over the limit", error.Message);
        Assert.Single(docker.RemovedImages);
    }

    [Fact]
    public async Task The_build_reports_progress_so_a_long_build_is_not_mistaken_for_a_dead_one()
    {
        var docker = new Docker();
        var beats = 0;

        await Builder(docker).BuildAsync(Request(), () => beats++, CancellationToken.None);

        Assert.Equal(4, beats);
    }
}

public sealed class RepoImageRunPolicyTests
{
    [Fact]
    public void A_run_may_use_a_repository_image_only_through_the_resolver()
    {
        var request = Support.Request(image: "repo-0123456789ab");
        var settings = Support.Settings(allowRepoImages: true);

        var run = SandboxPolicy.Validate(request, settings, _ => true, alias => alias == "repo-0123456789ab" ? "ai-sdlc-factory/repo:0123456789ab" : null);

        Assert.Equal("ai-sdlc-factory/repo:0123456789ab", run.Image);
        Assert.Throws<ApplicationFailureException>(() => SandboxPolicy.Validate(request, settings, _ => true));
        Assert.Throws<ApplicationFailureException>(() => SandboxPolicy.Validate(request, settings, _ => true, _ => null));
    }

    [Fact]
    public void An_alias_that_is_not_ours_is_never_resolved_even_with_a_resolver_that_says_yes()
    {
        var request = Support.Request(image: "ubuntu");

        Assert.Throws<ApplicationFailureException>(() => SandboxPolicy.Validate(request, Support.Settings(allowRepoImages: true), _ => true, _ => "ubuntu:latest"));
    }
}

public sealed class RepoImageSwitchTests
{
    private static SandboxSettings Read(string? value)
    {
        var values = new Dictionary<string, string?>
        {
            ["FACTORY_WORKSPACE_ROOT"] = "/work",
            ["SANDBOX_IMAGES"] = "dotnet=ai-sdlc-factory/sandbox-dotnet:local",
            ["SANDBOX_NETWORKS"] = "model=n1",
            ["SANDBOX_PROXIES"] = "model=http://10.0.0.2:3128",
            ["SANDBOX_EGRESS_MODE"] = "open",
        };
        if (value is not null)
        {
            values["FACTORY_ALLOW_REPO_IMAGES"] = value;
        }
        return SandboxSettings.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void Repository_images_are_off_unless_the_operator_says_true(string? value, bool expected) =>
        Assert.Equal(expected, Read(value).AllowRepoImages);

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    public void A_switch_that_is_not_true_or_false_stops_the_start(string value) =>
        Assert.Contains("must be true or false", Assert.Throws<InvalidOperationException>(() => Read(value)).Message);
}
