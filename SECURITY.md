# Security policy

AI SDLC Factory runs AI agents in containers beside a Docker daemon and holds git host tokens, so please report a vulnerability privately and not as a public issue.

## Reporting

Use the repository's **Security → Report a vulnerability** (GitHub's private vulnerability reporting). Say what you found, how to reproduce it and what it lets an attacker do. You will get an answer, and a fix is made before the details are made public.

## What is covered

The latest version on the default branch. There are no older supported versions.

Most interesting are ways to get out of an agent's sandbox, to make the egress proxy allow what it should not, to run something on the host through a repository's `.factory/` files or image, to act on a ticket without the permission the `commands` rules require, or to read a token or webhook secret.

How the parts are meant to be separated is in [Trust boundaries](docs/architecture.md#trust-boundaries) and [Security notes](docs/operations.md#security-notes). Running the factory on a machine you do not trust with your tokens, or giving agents more access than those documents say, is a deployment choice and not a vulnerability in the code.
