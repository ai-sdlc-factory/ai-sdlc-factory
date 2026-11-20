House rules for the agents of this project. Everything here adds to your role's rules and never relaxes them. Replace the examples with your own.

Names in this project: a *greeting* is the text built from a person's name; a *visitor* is the person being greeted. Use these words in code, tests and comments.

## developer
- Put a new rule in `greetings/greeting.py` as a pure function, and test it there first; `web.py` only translates HTTP.
- Test names say what happens (`test_an_unusable_name_is_refused`), one behaviour each.

## reviewer
- Reject a business rule that lives in `web.py`.
- Reject a bug fix that comes without a test that fails without it.

## security
- Names come from strangers: check that every new input is length-limited and that nothing a visitor typed reaches a log line unescaped.
