"""Opt-in test using an existing Temporal service, with an isolated task queue."""
import asyncio
import hashlib
import hmac
import json
import os
from uuid import uuid4

import httpx
import pytest
from temporalio.client import Client
from temporalio.worker import Worker

from factory_controller.api import create_app
from factory_controller.config import ApiSettings
from factory_controller.workflows import ForgejoEventWorkflow


@pytest.mark.skipif(
    not os.environ.get("TEMPORAL_TEST_ADDRESS"),
    reason="Set TEMPORAL_TEST_ADDRESS to run against a real Temporal service",
)
def test_webhook_completes_once_in_temporal():
    asyncio.run(run_webhook())


async def run_webhook():
    queue = f"controller-test-{uuid4()}"
    secret = "integration-test-secret-never-used-for-forgejo"
    settings = ApiSettings(
        temporal_address=os.environ["TEMPORAL_TEST_ADDRESS"],
        temporal_namespace=os.environ.get("TEMPORAL_TEST_NAMESPACE", "default"),
        temporal_task_queue=queue,
        forgejo_webhook_secret=secret,
    )
    client = await Client.connect(
        settings.temporal_address, namespace=settings.temporal_namespace
    )
    app = create_app(settings, client)
    body = json.dumps(
        {"repository": {"full_name": "test/controller"}, "action": "opened"}
    ).encode()
    headers = {
        "Content-Type": "application/json",
        "X-Forgejo-Event": "issues",
        "X-Forgejo-Delivery": str(uuid4()),
        "X-Forgejo-Signature": hmac.new(
            secret.encode(), body, hashlib.sha256
        ).hexdigest(),
    }
    async with Worker(client, task_queue=queue, workflows=[ForgejoEventWorkflow]):
        async with app.router.lifespan_context(app):
            async with httpx.AsyncClient(
                transport=httpx.ASGITransport(app=app), base_url="http://controller"
            ) as api:
                assert (await api.get("/health/ready")).status_code == 200
                response = await api.post("/webhooks/forgejo", content=body, headers=headers)
                assert response.status_code == 202, response.text
                workflow_id = response.json()["workflow_id"]
                result = await asyncio.wait_for(
                    client.get_workflow_handle(workflow_id).result(), timeout=20
                )
                assert result["event"] == "issues"
                assert result["repository"] == "test/controller"
                duplicate = await api.post("/webhooks/forgejo", content=body, headers=headers)
                assert duplicate.status_code == 200, duplicate.text
                assert duplicate.json()["status"] == "duplicate"
                assert duplicate.json()["workflow_id"] == workflow_id
                tampered = await api.post(
                    "/webhooks/forgejo", content=body+b" ", headers=headers
                )
                assert tampered.status_code == 401
