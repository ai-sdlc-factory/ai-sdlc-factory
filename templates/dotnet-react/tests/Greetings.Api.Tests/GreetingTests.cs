using Greetings.Api;

namespace Greetings.Api.Tests;

public class GreetingTests
{
    [Fact]
    public void A_usable_name_is_greeted()
    {
        Assert.Equal("Hello, Ada Lovelace!", Greeting.For("  Ada Lovelace "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<script>")]
    [InlineData("name; drop table")]
    public void An_unusable_name_is_refused(string? name)
    {
        Assert.Null(Greeting.For(name));
    }

    [Fact]
    public void A_name_over_the_limit_is_refused()
    {
        Assert.Null(Greeting.For(new string('a', Greeting.MaxNameLength + 1)));
        Assert.NotNull(Greeting.For(new string('a', Greeting.MaxNameLength)));
    }
}
