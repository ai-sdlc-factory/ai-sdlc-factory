"""Exercise separately deployed API and worker over HTTP and Temporal."""
import asyncio
import hashlib
import hmac
import json
import os
from uuid import uuid4

import httpx
from temporalio.client import Client


def test_signed_webhook_completes_once_in_temporal():
    asyncio.run(run_delivery())


async def run_delivery():
    secret = os.environ["FORGEJO_WEBHOOK_SECRET"]
    body = json.dumps(
        {"repository": {"full_name": "test/controller"}, "action": "opened"}
    ).encode()
    delivery_id = str(uuid4())
    headers = {
        "Content-Type": "application/json",
        "X-Forgejo-Event": "issues",
        "X-Forgejo-Delivery": delivery_id,
        "X-Forgejo-Signature": hmac.new(
            secret.encode(), body, hashlib.sha256
        ).hexdigest(),
    }
    temporal = await Client.connect(
        os.environ["TEMPORAL_TEST_ADDRESS"],
        namespace=os.environ.get("TEMPORAL_TEST_NAMESPACE", "default"),
    )
    async with httpx.AsyncClient(
        base_url=os.environ["CONTROLLER_TEST_URL"], timeout=10
    ) as api:
        assert (await api.get("/health/ready")).status_code == 200
        response = await api.post("/webhooks/forgejo", content=body, headers=headers)
        assert response.status_code == 202, response.text
        workflow_id = response.json()["workflow_id"]
        assert workflow_id == f"forgejo-{delivery_id}"
        result = await asyncio.wait_for(
            temporal.get_workflow_handle(workflow_id).result(), timeout=30
        )
        assert result == {
            "delivery_id": delivery_id,
            "event": "issues",
            "repository": "test/controller",
            "action": "opened",
            "ref": None,
        }
        duplicate = await api.post("/webhooks/forgejo", content=body, headers=headers)
        assert duplicate.status_code == 200, duplicate.text
        assert duplicate.json()["status"] == "duplicate"
        assert duplicate.json()["workflow_id"] == workflow_id
        tampered = await api.post(
            "/webhooks/forgejo", content=body+b" ", headers=headers
        )
        assert tampered.status_code == 401
