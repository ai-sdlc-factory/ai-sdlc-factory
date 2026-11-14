# Greetings API

## What this is
A tiny ASP.NET Core web API that greets a person by name. It exists to show how a .NET project looks when the factory works on it. Replace this section with what your project really is.

## Layout
- `src/Greetings.Api`: the web API. `Program.cs` maps the routes; `Greeting.cs` holds the business rule
- `tests/Greetings.Api.Tests`: xUnit tests, including API tests that start the app in memory
- `Greetings.slnx`: the solution; `dotnet build` and `dotnet test` at the root cover everything
- `e2e/Api.E2E`: end-to-end tests against the running API, written by the `/qa` run (not part of the solution)

## Build and test
- Build: `dotnet build --nologo -v q`
- Test: `dotnet test --nologo -v q`
- Run: `dotnet run --project src/Greetings.Api` (listens on http://localhost:5080)
The factory runs the commands under `gates` in `.factory/project.json`. If this file and the gates disagree, the gates win.

## Conventions
- .NET 10, C# with nullable reference types on. Warnings are errors: the build must be clean.
- Business rules live in plain classes (like `Greeting`), not in route handlers, so they are easy to test.
- Errors from bad input are `400` with a short JSON `{"error": "..."}`; never return `200` for a failure.
- Logging with `ILogger<T>` and message templates. Never log personal data.
- Configuration through `IConfiguration` or `IOptions<T>`; no `Environment.GetEnvironmentVariable` in application code.

## Testing
- xUnit. One behaviour per test, named after what happens (`An_unusable_name_is_refused`).
- Every change in behaviour comes with a test. API tests use `WebApplicationFactory<Program>`.
- A bug fix includes a test that fails without the fix.

## Do not touch
- `e2e/`: the end-to-end tests belong to the `/qa` run, and only it may change them. If one fails, fix the application, not the test.
- `**/Migrations/**` and `global.json` (the SDK version): a person changes these.
- A new NuGet package is fine when the ticket needs it; say why in the pull request.
