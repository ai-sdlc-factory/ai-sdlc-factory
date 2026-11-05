Use this as a checklist while you read the diff (OWASP Top 10 and API Security Top 10). Report an item only if the change introduces or exposes it, and only if you verified it by reading. The severity is the default; lower it when the code makes it harder to exploit, raise it when it is easier.

Access (A01, API1, API5): `blocker` if data can be read or changed by someone who should not be able to.
- A new route, handler or job runs without an authentication or authorisation check, or the check was removed or loosened.
- A record is looked up by an id from the request without checking that the caller owns it or may see it.
- The client says who it is or what role it has (a field, header or cookie) and the server believes it.
- A response or an update takes in or returns more fields than needed (mass assignment, over-sharing).

Injection and unsafe input (A03, A08): `blocker` if the input is attacker-controlled and reaches the sink.
- Input is put into a shell command, SQL, a file path, a URL, a template or HTML without parameters, escaping or an allowlist.
- Untrusted data is deserialised into objects (pickle, unsafe YAML, BinaryFormatter, `eval`, dynamic code loading).
- A file path from outside can leave its folder (`..`, absolute path, symbolic link) or an upload is stored or served unchecked.
- The server fetches a URL given by the caller without limiting hosts (SSRF), or redirects to one.

Secrets and crypto (A02, A07): `blocker` for an exposed secret, `major` otherwise.
- A credential, token or key is in code, config, tests, logs or an error message.
- Passwords or tokens are stored or compared in plain form, or with a fast hash, or with `==` where a constant-time comparison matters.
- A custom or weak algorithm, a fixed IV or salt, or a predictable random number is used where it must be unpredictable (tokens, ids, resets).
- Certificate, signature or host-name verification is turned off.

Configuration and exposure (A05, A06, API8): `major`.
- A debug mode, a permissive CORS rule, a wide-open port or a default account is added.
- Error messages or logs reveal stack traces, queries, internals or personal data.
- A new dependency or downloaded script is added without need, unpinned, or from an unusual source; build or CI files change permissions or add network access.

Resilience and traceability (A04, A09, API4): `major` when it makes abuse easy, otherwise `minor`.
- A new endpoint or loop takes unbounded input or work (no size, count, rate or timeout limit).
- A security-relevant event (login, permission change, deletion) leaves no log, or a log line records secrets.
