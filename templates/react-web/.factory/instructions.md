House rules for the agents of this project. Everything here adds to your role's rules and never relaxes them. Replace the examples with your own.

Names in this project: a *greeting* is the text built from a person's name; a *visitor* is the person being greeted. Use these words in code, tests and comments.

## developer
- Logic that is not about rendering goes in a plain module like `greeting.ts`, tested without React.
- Test what a person sees and does (`getByRole`, `userEvent`), never component internals.

## reviewer
- Reject a hard-coded colour; the colours are the CSS variables at the top of `styles.css`.
- Reject a bug fix that comes without a test that fails without it.

## ux
- Every screen that waits for something must show loading, empty, error and success; check each one is reachable and readable at 320 px wide.
