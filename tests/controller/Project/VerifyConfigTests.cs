using Xunit;

namespace AiSdlcFactory.Controller.Tests;

public sealed class VerifyConfigTests
{
    private static string Json(string verify) =>
        $$"""{"image":"dotnet-node","gates":[{"name":"test","command":["dotnet","test"]}],"verify":{{verify}}}""";

    private static VerifyConfig Parse(string verify)
    {
        Assert.True(ProjectConfigParser.TryParse(Json(verify), out var config, out var error), error);
        return config!.Verify!;
    }

    private static string Refused(string verify)
    {
        Assert.False(ProjectConfigParser.TryParse(Json(verify), out _, out var error));
        return error;
    }

    [Fact]
    public void A_project_without_verify_has_none()
    {
        Assert.True(ProjectConfigParser.TryParse("""{"image":"python","gates":[{"name":"t","command":["pytest"]}]}""", out var config, out _));
        Assert.Null(config!.Verify);
    }

    [Fact]
    public void A_full_setting_is_read_as_written()
    {
        var verify = Parse("""
            {"image":"browser","start":["dotnet","run","--urls","http://127.0.0.1:5080"],"start_cwd":"src/Api/","ready":"http://127.0.0.1:5080/health",
             "startup_timeout_seconds":90,"tests":"e2e/",
             "suites":[{"name":"api","command":["dotnet","test"],"cwd":"e2e/api","timeout_seconds":300},{"name":"ui","command":["npx","playwright","test"]}]}
            """);

        Assert.Equal("browser", verify.Image);
        Assert.Equal(["dotnet", "run", "--urls", "http://127.0.0.1:5080"], verify.Start);
        Assert.Equal("src/Api", verify.StartDirectory);
        Assert.Equal("http://127.0.0.1:5080/health", verify.Ready);
        Assert.Equal(90, verify.StartupSeconds);
        Assert.Equal("e2e", verify.Tests);
        Assert.Equal("e2e/evidence", verify.EvidenceDirectory);
        Assert.Equal("e2e/**", verify.ProtectedPattern);
        Assert.Equal(["api", "ui"], verify.Suites.Select(s => s.Name));
        Assert.Equal("e2e/api", verify.Suites[0].WorkingDirectory);
        Assert.Equal(300, verify.Suites[0].TimeoutSeconds);
        Assert.Equal(ProjectConfigParser.DefaultTimeoutSeconds, verify.Suites[1].TimeoutSeconds);
        Assert.True(verify.Has("ui"));
    }

    [Fact]
    public void Without_a_start_command_the_tests_start_the_application_themselves()
    {
        var verify = Parse("""{"tests":"e2e","suites":[{"name":"api","command":["npm","run","e2e"]}]}""");

        Assert.Null(verify.Start);
        Assert.Null(verify.Ready);
        Assert.Equal(ProjectConfigParser.DefaultStartupSeconds, verify.StartupSeconds);
    }

    [Theory]
    [InlineData("""{"start":["npm","start"],"tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "needs \"verify.ready\"")]
    [InlineData("""{"ready":"http://127.0.0.1:80/","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "only make sense with \"verify.start\"")]
    [InlineData("""{"start":["x"],"ready":"http://example.com/health","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "local address")]
    [InlineData("""{"start":["x"],"ready":"https://127.0.0.1:5080/","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "local address")]
    [InlineData("""{"start":["x"],"ready":"http://127.0.0.1.evil.com:5080/","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "local address")]
    [InlineData("""{"start":["x"],"ready":"http://127.0.0.1:5080/a b","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "local address")]
    [InlineData("""{"tests":"../e2e","suites":[{"name":"api","command":["x"]}]}""", "\"verify.tests\"")]
    [InlineData("""{"tests":".hidden","suites":[{"name":"api","command":["x"]}]}""", "\"verify.tests\"")]
    [InlineData("""{"tests":"/abs","suites":[{"name":"api","command":["x"]}]}""", "\"verify.tests\"")]
    [InlineData("""{"suites":[{"name":"api","command":["x"]}]}""", "\"verify.tests\"")]
    [InlineData("""{"tests":"e2e"}""", "\"verify.suites\"")]
    [InlineData("""{"tests":"e2e","suites":[]}""", "\"verify.suites\"")]
    [InlineData("""{"tests":"e2e","suites":[{"name":"api","command":["x"]},{"name":"ui","command":["x"]},{"name":"api2","command":["x"]}]}""", "\"verify.suites\"")]
    [InlineData("""{"tests":"e2e","suites":[{"name":"smoke","command":["x"]}]}""", "must be \"api\" or \"ui\"")]
    [InlineData("""{"tests":"e2e","suites":[{"name":"api","command":["x"]},{"name":"api","command":["y"]}]}""", "unique")]
    [InlineData("""{"tests":"e2e","suites":[{"name":"api","command":[]}]}""", "command")]
    [InlineData("""{"tests":"e2e","startup_timeout_seconds":1,"suites":[{"name":"api","command":["x"]}]}""", "between 5 and 600")]
    [InlineData("""{"tests":"e2e","image":"Bad Image","suites":[{"name":"api","command":["x"]}]}""", "\"verify.image\"")]
    [InlineData("""{"start":["x"],"start_cwd":"../up","ready":"http://127.0.0.1:80/","tests":"e2e","suites":[{"name":"api","command":["x"]}]}""", "start_cwd")]
    [InlineData("""[]""", "must be an object")]
    public void A_setting_that_cannot_be_used_is_refused_with_the_reason(string verify, string expected)
    {
        Assert.Contains(expected, Refused(verify));
    }

    [Fact]
    public void Localhost_with_a_path_and_query_is_a_valid_ready_address()
    {
        Assert.Equal("http://localhost:3000/api/health?full=1", Parse("""{"start":["npm","start"],"ready":"http://localhost:3000/api/health?full=1","tests":"e2e","suites":[{"name":"ui","command":["x"]}]}""").Ready);
    }
}
