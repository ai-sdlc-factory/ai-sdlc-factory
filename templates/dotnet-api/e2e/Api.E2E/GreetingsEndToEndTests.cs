using System.Net;
using System.Net.Http.Json;

namespace Api.E2E;

// Talks to the running application over HTTP, like a real client. The factory starts it (`verify.start`) before these run.
public class GreetingsEndToEndTests
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://127.0.0.1:5080") };

    private sealed record Message(string? message, string? error);

    [Fact]
    public async Task A_valid_name_is_greeted()
    {
        var response = await Client.GetAsync("/api/greetings/Ada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello, Ada!", (await response.Content.ReadFromJsonAsync<Message>())?.message);
    }

    [Fact]
    public async Task A_name_that_is_not_usable_is_refused()
    {
        var response = await Client.GetAsync("/api/greetings/" + new string('a', 51));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<Message>())?.error));
    }
}
