# Greetings web app

## What this is
A tiny React app where a person types a name and is greeted. It exists to show how a web front end looks when the factory works on it. Replace this section with what your app really is.

## Layout
- `src/App.tsx`: the one screen. `src/greeting.ts`: the rule that decides the greeting (pure, no React)
- `src/*.test.ts(x)`: Vitest tests. `src/styles.css`: styling with CSS variables
- `index.html`, `vite.config.ts`, `tsconfig.json`: the build setup

## Build and test
- Install: `npm ci`
- Types: `npm run typecheck`
- Test: `npm test`
- Build: `npm run build`
- Run: `npm run dev` (http://127.0.0.1:5173)
The factory runs the commands under `gates` in `.factory/project.json`. There is no browser available to the agents; component tests use jsdom. If this file and the gates disagree, the gates win.

## Conventions
- React 19 with function components and hooks, TypeScript with `strict` on. No `any`; use `unknown` and narrow it.
- Logic that is not about rendering goes in plain modules like `greeting.ts`, so it can be tested without React.
- Styling with the CSS variables at the top of `styles.css`; no hard-coded colours.
- No new dependencies unless the ticket needs one; say why in the pull request.

## Accessibility and UX
- Every input has a visible `<label>`; results are announced with `role="status"`.
- Use real `<button>` and `<a>` elements; everything works with the keyboard, with a visible focus ring.
- Do not carry meaning by colour alone; text contrast at least 4.5:1.
- Every screen that waits for data handles loading, empty, error and success.
- Layouts work from 320 px wide.

## Testing
- Vitest and Testing Library. Test what a person sees and does (`getByRole`, `userEvent`), not implementation details.
- Every change in behaviour comes with a test. A bug fix includes a test that fails without the fix.

## Do not touch
- `vite.config.ts` and `tsconfig.json`: a person changes these.
