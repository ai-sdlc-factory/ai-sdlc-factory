House rules for the agents of this project. Everything here adds to your role's rules and never relaxes them. Replace the examples with your own.

Names in this project: a *greeting* is the text built from a person's name; a *visitor* is the person being greeted. Use these words in code, tests and comments.

## developer
- Put a new rule in a plain class like `Greeting`, with an xUnit test beside it; route handlers only translate HTTP.
- Test names say what happens (`An_unusable_name_is_refused`), one behaviour each. API tests use `WebApplicationFactory<Program>`.

## reviewer
- Reject a business rule inside a route handler.
- Reject a bug fix that comes without a test that fails without it.

## security
- Names come from strangers: check that every new input is length-limited and that nothing a visitor typed reaches a log message other than as a template argument.
