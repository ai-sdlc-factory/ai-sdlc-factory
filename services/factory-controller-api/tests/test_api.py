import hashlib
import hmac
from unittest.mock import AsyncMock, MagicMock
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError
from temporalio.client import Client
from temporalio.common import WorkflowIDConflictPolicy, WorkflowIDReusePolicy
from temporalio.exceptions import WorkflowAlreadyStartedError
from temporalio.service import RPCError, RPCStatusCode

from factory_controller_api.api import create_app
from factory_controller_api.config import ApiSettings

SECRET = "controller-test-secret-never-used-outside-tests"
BODY = b'{ "repository": {"full_name": "owner/repo"}, "ref": "refs/heads/main" }'


def signed_headers(body=BODY, delivery=None):
    return {
        "Content-Type": "application/json",
        "X-Forgejo-Event": "push",
        "X-Forgejo-Delivery": delivery or str(uuid4()),
        "X-Forgejo-Signature": hmac.new(SECRET.encode(), body, hashlib.sha256).hexdigest(),
    }


@pytest.fixture
def temporal():
    client = MagicMock(spec=Client)
    client.start_workflow = AsyncMock()
    client.workflow_service.describe_namespace = AsyncMock()
    return client


@pytest.fixture
def api(temporal):
    settings = ApiSettings(forgejo_webhook_secret=SECRET)
    with TestClient(create_app(settings, temporal)) as client:
        yield client


def test_signed_event_is_durable_before_acceptance(api, temporal):
    headers = signed_headers()
    response = api.post("/webhooks/forgejo", content=BODY, headers=headers)
    assert response.status_code == 202
    assert response.json()["status"] == "accepted"
    assert response.json()["workflow_id"] == f"forgejo-{headers['X-Forgejo-Delivery']}"
    args, kwargs = temporal.start_workflow.call_args
    assert args[0] == "ForgejoEventWorkflow"
    assert args[1]["payload"]["repository"]["full_name"] == "owner/repo"
    assert args[1]["event"] == "push"
    assert kwargs["task_queue"] == "factory-events"
    assert kwargs["id_reuse_policy"] == WorkflowIDReusePolicy.REJECT_DUPLICATE
    assert kwargs["id_conflict_policy"] == WorkflowIDConflictPolicy.FAIL


@pytest.mark.parametrize("signature", [None, "", "bad", "0" * 64])
def test_invalid_signature_never_starts_work(api, temporal, signature):
    headers = signed_headers()
    if signature is None:
        del headers["X-Forgejo-Signature"]
    else:
        headers["X-Forgejo-Signature"] = signature
    assert api.post("/webhooks/forgejo", content=BODY, headers=headers).status_code == 401
    temporal.start_workflow.assert_not_awaited()


def test_signature_uses_exact_original_bytes(api, temporal):
    headers = signed_headers()
    changed = BODY.replace(b"owner/repo", b"owner/evil")
    assert api.post("/webhooks/forgejo", content=changed, headers=headers).status_code == 401
    temporal.start_workflow.assert_not_awaited()


@pytest.mark.parametrize("body", [b"{bad", b"[]", b"null", b'{"value": NaN}', b"\xff"])
def test_invalid_json_never_starts_work(api, temporal, body):
    response = api.post("/webhooks/forgejo", content=body, headers=signed_headers(body))
    assert response.status_code == 400
    temporal.start_workflow.assert_not_awaited()


@pytest.mark.parametrize(
    ("name", "value"),
    [
        ("X-Forgejo-Delivery", ""),
        ("X-Forgejo-Delivery", "invalid"),
        ("X-Forgejo-Event", ""),
        ("X-Forgejo-Event", "../unexpected"),
    ],
)
def test_invalid_headers_never_start_work(api, temporal, name, value):
    headers = signed_headers()
    headers[name] = value
    assert api.post("/webhooks/forgejo", content=BODY, headers=headers).status_code == 400
    temporal.start_workflow.assert_not_awaited()


def test_non_json_content_type_is_rejected(api, temporal):
    headers = signed_headers()
    headers["Content-Type"] = "application/x-www-form-urlencoded"
    assert api.post("/webhooks/forgejo", content=BODY, headers=headers).status_code == 415
    temporal.start_workflow.assert_not_awaited()


def test_streamed_body_limit_does_not_depend_on_content_length(temporal):
    settings = ApiSettings(forgejo_webhook_secret=SECRET, max_webhook_bytes=8)
    with TestClient(create_app(settings, temporal)) as client:
        response = client.post(
            "/webhooks/forgejo", content=iter([BODY[:10], BODY[10:]]), headers=signed_headers()
        )
    assert response.status_code == 413
    temporal.start_workflow.assert_not_awaited()


def test_duplicate_delivery_is_acknowledged(api, temporal):
    temporal.start_workflow.side_effect = WorkflowAlreadyStartedError(
        "existing-workflow", "ForgejoEventWorkflow"
    )
    response = api.post("/webhooks/forgejo", content=BODY, headers=signed_headers())
    assert response.status_code == 200
    assert response.json()["status"] == "duplicate"


@pytest.mark.parametrize(
    "failure",
    [RPCError("unavailable", RPCStatusCode.UNAVAILABLE, b""), TimeoutError()],
)
def test_temporal_failure_does_not_acknowledge_delivery(api, temporal, failure):
    temporal.start_workflow.side_effect = failure
    response = api.post("/webhooks/forgejo", content=BODY, headers=signed_headers())
    assert response.status_code == 503
    assert "retry" in response.json()["detail"]


def test_health_checks(api, temporal):
    assert api.get("/health/live").status_code == 200
    assert api.get("/health/ready").status_code == 200
    temporal.workflow_service.describe_namespace.side_effect = RPCError(
        "unavailable", RPCStatusCode.UNAVAILABLE, b""
    )
    assert api.get("/health/ready").status_code == 503
    assert api.get("/health/live").status_code == 200


def test_secret_must_be_configured_without_exposing_it():
    invalid_secret = "test-secret-too-short"
    with pytest.raises(ValidationError) as error:
        ApiSettings(forgejo_webhook_secret=invalid_secret)
    assert invalid_secret not in str(error.value)
    assert "forgejo_webhook_secret" in str(error.value)


def test_openapi_is_available(api):
    schema = api.get("/openapi.json").json()
    assert "/webhooks/forgejo" in schema["paths"]


def test_api_does_not_install_the_worker():
    import importlib.util

    assert importlib.util.find_spec("factory_controller_worker") is None
