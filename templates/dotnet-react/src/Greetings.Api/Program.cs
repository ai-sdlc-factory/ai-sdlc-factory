using Greetings.Api;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// GET /api/greetings/Ada  ->  200 {"message":"Hello, Ada!"}   (400 for a name that is not usable)
app.MapGet("/api/greetings/{name}", (string name) =>
    Greeting.For(name) is { } message
        ? Results.Ok(new { message })
        : Results.BadRequest(new { error = $"The name must be 1 to {Greeting.MaxNameLength} letters, digits, spaces, dots or dashes." }));

app.Run();

// Lets the tests start the app in memory.
public partial class Program;
