# Acme Orders

## What this is
Acme Orders is the order and invoicing API for our webshop, plus a small admin UI. Customers never use it directly; the shop front end and our staff do.

## Layout
- `src/Acme.Orders.Api`: ASP.NET Core minimal API, endpoints in `Endpoints/`, one file per feature
- `src/Acme.Orders.Domain`: entities and business rules, no framework references
- `src/Acme.Orders.Infrastructure`: EF Core, repositories, external clients
- `tests/Acme.Orders.UnitTests` and `tests/Acme.Orders.IntegrationTests`
- `web/admin`: React + TypeScript admin UI (Vite)
- `db/migrations`: EF Core migrations, **generated and reviewed by a person**

## Build and test
- Build: `dotnet build --nologo -v q`
- Test: `dotnet test --nologo -v q`
- UI: `cd web/admin && npm ci && npm test`
The factory runs the commands listed under `gates` in `.factory/project.json`; if this file and the gates disagree, the gates win.

## Conventions
- .NET 9, C# with nullable reference types on. No warnings allowed.
- One public type per file; file name equals type name.
- Async all the way: `async`/`await`, `CancellationToken` passed down from the endpoint.
- Errors: throw domain exceptions in `Domain`, translate to `ProblemDetails` in the API layer. Never return `null` for "not found".
- Logging with `ILogger<T>` and message templates. Never log customer emails, addresses or payment data.
- Configuration through `IOptions<T>`; no `Environment.GetEnvironmentVariable` in application code.
- Dates are `DateTimeOffset` in UTC.

## Architecture rules
- `Domain` references nothing. `Infrastructure` and `Api` reference `Domain`. `Api` does not reference `Infrastructure` types directly (only through interfaces registered in `Program.cs`).
- Business rules live in `Domain`, not in endpoints or EF configuration.
- New endpoints are added as a new file in `Endpoints/` and registered in `Program.cs`. Every endpoint declares `RequireAuthorization()` or a justified `AllowAnonymous()`.

## Testing
- xUnit and FluentAssertions. One behaviour per test, named `Method_condition_result`.
- Unit tests for domain rules; integration tests for endpoints, using the in-memory test server and a throwaway database.
- A bug fix includes a test that fails without the fix.
- Do not mock what you own; fake external HTTP clients with the helpers in `tests/Common`.

## UI and UX
- Use the components in `web/admin/src/components` before writing new ones.
- Every input has a label; every button is a real `<button>`; the UI works with the keyboard.
- Text goes through `t()` (i18n); no hard-coded strings in components.
- Colours and spacing come from the tokens in `web/admin/src/theme.ts`.

## Do not touch
- `db/migrations/**` and `Migrations/` folders: a person creates migrations.
- `deploy/**`, `.github/**`, `Dockerfile`, `Directory.Packages.props`: infrastructure and dependency versions.
- Generated files: `**/*.g.cs`, `web/admin/src/api/generated/**`.
- Do not add a NuGet or npm dependency without saying why in the pull request.
