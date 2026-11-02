# Integration Tests

[Project overview](../../README.md) | [API](../../services/api/README.md) | [Controller](../../services/controller/README.md)

Tests the deployed API and controller through HTTP and Temporal. This test image
does not import either service's application code.

## Run

From the repository root:

```bash
cd infra
docker compose up -d --build api controller
docker compose --profile test run --build --rm --no-deps integration-tests
```

The test profile is excluded from normal stack startup. Compose supplies only
the API URL, Temporal connection settings, and shared webhook signing secret.

The test sends a synthetic signed issue event with a unique delivery ID, waits
for the real controller to complete it, checks duplicate handling after completion,
and verifies that a tampered request is rejected. It creates one workflow in the
configured namespace and requires no Forgejo repository.
