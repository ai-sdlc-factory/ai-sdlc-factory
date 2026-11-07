# Greetings (API + web)

## What this is
A tiny full-stack app: a .NET API that builds a greeting and a React page that asks it for one. It exists to show how a project with two parts looks when the factory works on it. Replace this section with what your project really is.

## Layout
- `src/Greetings.Api`: ASP.NET Core API. `Program.cs` maps routes; `Greeting.cs` holds the business rule
- `tests/Greetings.Api.Tests`: xUnit tests, including API tests that start the app in memory
- `frontend/`: Vite + React + TypeScript. `src/api.ts` is the only code that talks to the API; `src/App.tsx` is the screen
- `Greetings.slnx`: the .NET solution; `dotnet build` and `dotnet test` at the root cover the API

## Build and test
- API: `dotnet build --nologo -v q` and `dotnet test --nologo -v q` (from the root)
- Web: `cd frontend && npm ci && npm run typecheck && npm test && npm run build`
- Run: `dotnet run --project src/Greetings.Api` (http://localhost:5080), then `cd frontend && npm run dev` (http://127.0.0.1:5173, which forwards `/api` to the API)
The factory runs the commands under `gates` in `.factory/project.json` (the web ones inside `frontend/`). If this file and the gates disagree, the gates win. The sandbox has no Docker and no database or cache server: tests must use in-memory fakes.

## Conventions
- API: .NET 10, nullable on, warnings are errors. Business rules in plain classes, not in route handlers. Bad input is a `400` with `{"error": "..."}`.
- Web: React function components and hooks, TypeScript `strict`, no `any`. Components never call `fetch` directly; they use `src/api.ts`.
- The contract between them is the route `GET /api/greetings/{name}` returning `{"message": "..."}` or `{"error": "..."}`. Change both sides and both sets of tests together.
- No new dependencies unless the ticket needs one; say why in the pull request.

## Accessibility and UX
- Every input has a visible `<label>`; results are announced with `role="status"`; real buttons; everything works with the keyboard.
- Handle loading, error and success states. Text contrast at least 4.5:1. Layouts work from 320 px wide.

## Testing
- xUnit for the API, Vitest and Testing Library for the web. Test behaviour, not implementation details. Every change in behaviour comes with a test; a bug fix includes a test that fails without the fix.

## Do not touch
- `**/Migrations/**`, `Directory.Packages.props`, `global.json`, `*.lock`, `frontend/package-lock.json`, `frontend/vite.config.ts`: a person changes these.
