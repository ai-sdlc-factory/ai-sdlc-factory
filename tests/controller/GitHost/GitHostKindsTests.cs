namespace DevFoundry.Controller.Tests;

public sealed class GitHostKindsTests
{
    [Fact]
    public void Every_kind_of_host_the_webhooks_know_has_a_client_and_the_other_way_round()
    {
        // A kind with an adapter but no client would pass the settings and then fail at the first call; a client no adapter can feed would never be used.
        Assert.Equal(WebhookAdapters.All.Select(adapter => adapter.Type).Order(), GitHostKinds.All.Select(kind => kind.Type).Order());
    }

    [Fact]
    public void Each_kind_makes_the_client_of_its_own_host()
    {
        var forgejo = new HostSettings("a", "forgejo", "http://forgejo:3000/", "t", "bot").CreateClient();
        var github = new HostSettings("b", "github", "https://github.com", "t", "bot").CreateClient();

        Assert.IsType<ForgejoClient>(forgejo);
        Assert.IsType<GitHubClient>(github);
    }

    [Fact]
    public void A_type_without_a_client_is_refused_by_name()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new HostSettings("a", "gitlab", "https://gitlab.com", "t", "bot").ApiBase);

        Assert.Contains("gitlab", error.Message);
    }
}
