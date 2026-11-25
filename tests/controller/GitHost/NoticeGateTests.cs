using System.Net;
using System.Text.Json;

namespace AiSdlcFactory.Controller.Tests;

/// <summary>
/// Events that reach the factory together must not all post the same notice. A ticket created with three labels sends four events, each handled at the same time; without the gate each of the four
/// found the notice missing and posted it (seen on a real ticket). The fake here keeps what is posted, and answers slowly, so the checks and the posts of different callers overlap.
/// </summary>
public sealed class NoticeGateTests
{
    private sealed class Ticket : HttpMessageHandler
    {
        private readonly List<string> comments = [];
        private readonly object gate = new();

        public int Posts { get; private set; }

        public IReadOnlyList<string> Comments { get { lock (gate) { return [.. comments]; } } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken); // long enough for the other callers to get to their own look
            if (request.Method == HttpMethod.Post)
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("body").GetString()!;
                lock (gate)
                {
                    Posts++;
                    comments.Add(body);
                }
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
            }
            string list;
            lock (gate)
            {
                list = JsonSerializer.Serialize(comments.Select(body => new { body }));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list) };
        }
    }

    private static (IGitHost Client, Ticket Ticket) GitHub()
    {
        var ticket = new Ticket();
        return (new GitHubClient(new HttpClient(ticket) { BaseAddress = new Uri("https://api.github.com/") }, "secret-token"), ticket);
    }

    private static (IGitHost Client, Ticket Ticket) Forgejo()
    {
        var ticket = new Ticket();
        return (new ForgejoClient(new HttpClient(ticket) { BaseAddress = new Uri("http://forgejo:3000/") }, "secret-token"), ticket);
    }

    public static IEnumerable<object[]> Hosts() => [["github"], ["forgejo"]];

    private static (IGitHost Client, Ticket Ticket) Host(string kind) => kind == "github" ? GitHub() : Forgejo();

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Events_that_arrive_together_post_a_unique_notice_once(string kind)
    {
        var (client, ticket) = Host(kind);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostNoticeUniqueAsync("acme", "shop", 7, "A run starts only when set off by a maintainer.", CancellationToken.None)));

        Assert.Equal(1, ticket.Posts);
        Assert.Single(results, posted => posted);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Events_that_arrive_together_post_a_notice_once(string kind)
    {
        var (client, ticket) = Host(kind);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostNoticeOnceAsync("acme", "shop", 7, "No QA run is waiting for a go-ahead.", CancellationToken.None)));

        Assert.Equal(1, ticket.Posts);
        Assert.Single(results, posted => posted);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Different_notices_that_arrive_together_are_each_posted_once(string kind)
    {
        var (client, ticket) = Host(kind);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => client.PostNoticeUniqueAsync("acme", "shop", 7, i % 2 == 0 ? "First notice." : "Second notice.", CancellationToken.None)));

        Assert.Equal(2, ticket.Posts);
        Assert.Equal(1, ticket.Comments.Count(comment => comment.Contains("First notice.")));
        Assert.Equal(1, ticket.Comments.Count(comment => comment.Contains("Second notice.")));
    }

    [Fact]
    public async Task One_ticket_at_a_time_and_a_turn_that_is_cancelled_while_waiting_does_not_leave_the_gate_shut()
    {
        var first = await NoticeGate.EnterAsync("acme", "shop", 7, CancellationToken.None);
        using var cancel = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoticeGate.EnterAsync("acme", "shop", 7, cancel.Token));
        first.Dispose();
        first.Dispose(); // letting go twice does not let two in

        using var again = await NoticeGate.EnterAsync("acme", "shop", 7, CancellationToken.None);
        var waiting = NoticeGate.EnterAsync("acme", "shop", 7, CancellationToken.None);
        Assert.False(waiting.IsCompleted); // the gate is held by "again"
        again.Dispose();
        (await waiting).Dispose();
    }
}
