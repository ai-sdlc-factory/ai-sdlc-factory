# Acme Storefront (web)

## What this is
The customer-facing web shop for Acme: product list, product page, cart and checkout. It talks to the Orders API over HTTP. Used on phones more than on desktops.

## Layout
- `src/pages`: one folder per route (`ProductPage/`, `CartPage/`), each with its component, hook and test
- `src/components`: shared building blocks (Button, TextField, Modal, Toast). Look here first
- `src/api`: the typed API client. All backend calls go through it
- `src/theme.ts`: colour, spacing and typography tokens
- `src/i18n`: translation files, one JSON per language
- `e2e`: Playwright tests (not run by the factory's checks)

## Build and test
- Install and test: `npm ci && npm test`
- Lint and types: `npm run lint && npm run typecheck`
- Build: `npm run build`
The factory runs the commands listed under `gates` in `.factory/project.json`. Component tests run with Vitest and Testing Library; there is no browser available to the agents.

## Conventions
- React 18, TypeScript with `strict` on. No `any`; use `unknown` and narrow it.
- Function components and hooks only. One component per file; the file name equals the component name.
- State: local state first; shared state in the existing store (`src/store`). Do not add a new state library.
- Data fetching through the hooks in `src/api`; components never call `fetch` directly.
- Styling with the tokens in `src/theme.ts`. No hard-coded colours, pixel spacing or font sizes.
- Text goes through `t('key')`. Add the key to every language file, not only English.
- Money and dates are formatted with the helpers in `src/format`, never by hand.

## Accessibility and UX
- Every input has a visible label. Placeholder text is not a label.
- Use real `<button>` and `<a>` elements; no clickable `<div>`. Everything must work with the keyboard, in a sensible tab order, with a visible focus ring.
- Images have `alt` text; decorative images use `alt=""`.
- Do not carry meaning by colour alone. Text contrast at least 4.5:1; touch targets at least 44 px.
- Every screen that loads data handles four states: loading, empty, error (say what happened and what to do) and success.
- Destructive actions (remove from cart, cancel order) ask for confirmation or offer undo.
- Layouts work from 320 px wide. Test long product names and missing images.

## Testing
- Vitest and Testing Library. Test what a person sees and does (`getByRole`, `userEvent`), not implementation details or CSS class names.
- Each new component has tests for its states (loading, empty, error) and its keyboard use.
- No snapshot tests of large trees. Mock the API client, not `fetch`.
- A bug fix includes a test that fails without the fix.

## Do not touch
- `src/api/generated/**`: generated from the OpenAPI file; change the spec, not these files
- `public/locales/**`: the translation team's tool is the source of truth; add keys only in `src/i18n`
- `.github/**` and `vite.config.ts` unless the ticket is about the build
- Do not add an npm dependency without saying why in the pull request.
