using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DevFoundry.AlertIntake.Tests;

public sealed class MetricsTests
{
    [Fact]
    public async Task Every_outcome_is_counted_and_every_series_exists_from_the_start()
    {
        var settings = Setup.Settings(perHour: 1);
        var metrics = new IntakeMetrics(settings, RouteTable.Parse(Setup.WeatherRoutes));
        var forgejo = new FakeForgejo();
        var processor = Setup.Processor(forgejo, settings, metrics: metrics);
        Assert.Contains("alert_intake_outcomes_total{action=\"created\"} 0", metrics.Render()); // a rate() needs the series before the first event

        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing(), Setup.Firing("Two", fingerprint: "bbbb"))), default);
        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Firing())), default);
        await processor.ProcessAsync(Setup.Parse(Setup.Message(Setup.Resolved())), default);

        var text = metrics.Render();
        Assert.Contains("alert_intake_outcomes_total{action=\"created\"} 1", text);
        Assert.Contains("alert_intake_outcomes_total{action=\"rate_limited\"} 1", text);
        Assert.Contains("alert_intake_outcomes_total{action=\"duplicate\"} 1", text);
        Assert.Contains("alert_intake_outcomes_total{action=\"commented\"} 1", text);
        Assert.Contains("alert_intake_dry_run 0", text);
        Assert.Contains("alert_intake_routes 1", text);
        Assert.Contains("alert_intake_max_tickets_per_hour 1", text);
    }

    [Fact]
    public async Task The_metrics_endpoint_counts_notifications_and_refusals_and_carries_no_alert_text()
    {
        var forgejo = new FakeForgejo();
        var settings = Setup.Settings(dryRun: true);
        using var client = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(settings);
                services.AddSingleton(RouteTable.Parse(Setup.WeatherRoutes));
                services.AddSingleton(new RateLimiter(6, TimeProvider.System));
                services.AddSingleton(Setup.Hosts(forgejo));
            })).CreateClient();
        HttpRequestMessage Post(string body, string token) => new(HttpMethod.Post, "/alerts")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) },
        };

        (await client.SendAsync(Post(Setup.Message(Setup.Firing(summary: "SECRET-SUMMARY")), Setup.Token))).Dispose();
        (await client.SendAsync(Post("not json", Setup.Token))).Dispose();
        (await client.SendAsync(Post("{}", "wrong-token-wrong-token"))).Dispose();
        using var response = await client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("alert_intake_notifications_total 1", text);
        Assert.Contains("alert_intake_alerts_total 1", text);
        Assert.Contains("alert_intake_outcomes_total{action=\"dry_run\"} 1", text);
        Assert.Contains("alert_intake_rejected_total{reason=\"invalid\"} 1", text);
        Assert.Contains("alert_intake_rejected_total{reason=\"unauthorized\"} 1", text);
        Assert.Contains("alert_intake_dry_run 1", text);
        Assert.DoesNotContain("SECRET-SUMMARY", text);
    }
}
