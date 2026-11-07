using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Greetings.Api.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task A_greeting_comes_back_as_json()
    {
        var response = await client.GetAsync("/api/greetings/Ada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Hello, Ada!", body!["message"]);
    }

    [Fact]
    public async Task An_unusable_name_is_a_400_with_an_error()
    {
        var response = await client.GetAsync("/api/greetings/%3Cscript%3E");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must be", await response.Content.ReadAsStringAsync());
    }
}
