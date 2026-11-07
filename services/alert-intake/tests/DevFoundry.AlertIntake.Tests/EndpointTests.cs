using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DevFoundry.AlertIntake.Tests;

public sealed class EndpointTests
{
    private readonly FakeForgejo forgejo = new();

    private HttpClient Client(IntakeSettings? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                settings ??= Setup.Settings();
                services.AddSingleton(settings);
                services.AddSingleton(RouteTable.Parse(Setup.WeatherRoutes));
                services.AddSingleton(new RateLimiter(settings.MaxTicketsPerHour, TimeProvider.System));
                services.AddSingleton<IForgejo>(forgejo);
                services.AddSingleton(new IntakeMetrics(settings, RouteTable.Parse(Setup.WeatherRoutes)));
            })).CreateClient();

    private static HttpRequestMessage Post(string body, string? token = Setup.Token) => new(HttpMethod.Post, "/alerts")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
        Headers = { Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token) },
    };

    [Fact]
    public async Task A_notification_with_the_token_opens_a_ticket_and_says_what_it_did()
    {
        using var client = Client();

        using var response = await client.SendAsync(Post(Setup.Message(Setup.Firing())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"created\"", await response.Content.ReadAsStringAsync());
        Assert.Single(forgejo.Tickets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token-wrong-token")]
    [InlineData("")]
    public async Task Without_the_right_token_nothing_happens(string? token)
    {
        using var client = Client();

        using var response = await client.SendAsync(Post(Setup.Message(Setup.Firing()), token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(forgejo.Tickets);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task A_body_that_is_not_a_notification_is_a_400(string body)
    {
        using var client = Client();

        using var response = await client.SendAsync(Post(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_a_413_whether_or_not_its_length_is_announced()
    {
        using var client = Client(Setup.Settings(maxBody: 1_024));
        var big = Setup.Message(Setup.Firing(description: new string('x', 3_000)));

        using var announced = await client.SendAsync(Post(big));
        var chunked = Post(big);
        chunked.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        chunked.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        chunked.Headers.TransferEncodingChunked = true;
        using var unannounced = await client.SendAsync(chunked);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, announced.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, unannounced.StatusCode);
        Assert.Empty(forgejo.Tickets);
    }

    [Fact]
    public async Task A_failing_forgejo_is_a_502_so_that_alertmanager_sends_the_notification_again()
    {
        forgejo.Failure = new HttpRequestException("Forgejo refused the ticket request: 500");
        using var client = Client();

        using var response = await client.SendAsync(Post(Setup.Message(Setup.Firing())));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task The_health_endpoint_needs_no_token_and_says_whether_it_is_a_dry_run()
    {
        using var client = Client(Setup.Settings(dryRun: true));

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"dryRun\":true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void The_token_check_is_exact()
    {
        Assert.True(Endpoints.Authorized("Bearer abc", "abc"));
        Assert.False(Endpoints.Authorized("Bearer abcd", "abc"));
        Assert.False(Endpoints.Authorized("abc", "abc"));
        Assert.False(Endpoints.Authorized("bearer abc", "abc"));
    }
}
