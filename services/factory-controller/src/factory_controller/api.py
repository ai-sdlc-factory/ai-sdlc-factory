import asyncio
import hashlib
import hmac
import json
import logging
import re
from contextlib import asynccontextmanager
from datetime import timedelta
from typing import Annotated, cast
from uuid import UUID

from fastapi import Depends, FastAPI, Header, HTTPException, Request, Response
from temporalio.api.workflowservice.v1 import DescribeNamespaceRequest
from temporalio.client import Client
from temporalio.common import WorkflowIDConflictPolicy, WorkflowIDReusePolicy
from temporalio.exceptions import WorkflowAlreadyStartedError
from temporalio.service import RPCError

from .config import ApiSettings
from .models import ForgejoEvent, WebhookReceipt
from .workflows import ForgejoEventWorkflow

logger = logging.getLogger(__name__)


def get_client(request: Request) -> Client:
    return cast(Client, request.app.state.temporal_client)


def reject_json_constant(value: str) -> None:
    raise ValueError("Non-standard JSON constant")


def create_app(
    settings: ApiSettings | None = None, temporal_client: Client | None = None
) -> FastAPI:
    settings = settings if settings is not None else ApiSettings()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        app.state.temporal_client = (
            temporal_client
            if temporal_client is not None
            else await asyncio.wait_for(
                Client.connect(
                    settings.temporal_address, namespace=settings.temporal_namespace
                ),
                timeout=10,
            )
        )
        yield

    app = FastAPI(title="Factory Controller API", version="0.1.0", lifespan=lifespan)

    @app.get("/health/live", tags=["Health"])
    async def live() -> dict[str, str]:
        return {"status": "ok"}

    @app.get("/health/ready", tags=["Health"])
    async def ready(client: Annotated[Client, Depends(get_client)]) -> dict[str, str]:
        try:
            await client.workflow_service.describe_namespace(
                DescribeNamespaceRequest(namespace=settings.temporal_namespace),
                timeout=timedelta(seconds=2),
            )
        except (RPCError, TimeoutError):
            raise HTTPException(503, "Temporal is unavailable") from None
        return {"status": "ready"}

    @app.post(
        "/webhooks/forgejo",
        status_code=202,
        response_model=WebhookReceipt,
        tags=["Webhooks"],
        summary="Verify a Forgejo delivery and durably start its receipt workflow",
        responses={
            200: {"description": "Delivery already recorded"},
            400: {"description": "Invalid headers or JSON body"},
            401: {"description": "Missing or invalid webhook signature"},
            413: {"description": "Payload exceeds one MiB"},
            415: {"description": "Expected application/json"},
            503: {"description": "Temporal did not acknowledge the delivery"},
        },
    )
    async def forgejo_webhook(
        request: Request,
        response: Response,
        client: Annotated[Client, Depends(get_client)],
        signature: Annotated[str | None, Header(alias="X-Forgejo-Signature")] = None,
        delivery: Annotated[str | None, Header(alias="X-Forgejo-Delivery")] = None,
        event: Annotated[str | None, Header(alias="X-Forgejo-Event")] = None,
    ) -> WebhookReceipt:
        if not signature or not re.fullmatch(r"[0-9a-fA-F]{64}", signature):
            raise HTTPException(401, "Missing or invalid webhook signature")

        chunks = []
        size = 0
        async for chunk in request.stream():
            size += len(chunk)
            if size > settings.max_webhook_bytes:
                raise HTTPException(413, "Webhook payload is too large")
            chunks.append(chunk)
        body = b"".join(chunks)
        expected = hmac.digest(
            settings.forgejo_webhook_secret.get_secret_value().encode(),
            body,
            hashlib.sha256,
        )
        if not hmac.compare_digest(expected, bytes.fromhex(signature)):
            raise HTTPException(401, "Missing or invalid webhook signature")

        content_type = request.headers.get("content-type", "").split(";", 1)[0].strip()
        if content_type.lower() != "application/json":
            raise HTTPException(415, "Expected application/json")

        if not event or not re.fullmatch(r"[a-z][a-z0-9_]{0,63}", event):
            raise HTTPException(400, "Missing or invalid X-Forgejo-Event")
        try:
            delivery_id = str(UUID(delivery or ""))
        except ValueError:
            raise HTTPException(400, "Missing or invalid X-Forgejo-Delivery") from None

        try:
            payload = json.loads(body, parse_constant=reject_json_constant)
        except (ValueError, UnicodeDecodeError, RecursionError):
            raise HTTPException(400, "Expected a valid JSON object") from None
        if not isinstance(payload, dict):
            raise HTTPException(400, "Expected a JSON object")

        workflow_id = f"forgejo-{delivery_id}"
        envelope = ForgejoEvent(delivery_id=delivery_id, event=event, payload=payload)
        duplicate = False
        try:
            await client.start_workflow(
                ForgejoEventWorkflow.run,
                envelope,
                id=workflow_id,
                task_queue=settings.temporal_task_queue,
                id_reuse_policy=WorkflowIDReusePolicy.REJECT_DUPLICATE,
                id_conflict_policy=WorkflowIDConflictPolicy.FAIL,
                rpc_timeout=timedelta(seconds=3),
            )
        except WorkflowAlreadyStartedError:
            duplicate = True
            response.status_code = 200
        except (RPCError, TimeoutError):
            logger.warning("Temporal did not acknowledge webhook delivery %s", delivery_id)
            raise HTTPException(503, "Temporal is unavailable; retry this delivery") from None

        return WebhookReceipt(
            status="duplicate" if duplicate else "accepted",
            workflow_id=workflow_id,
            delivery_id=delivery_id,
            event=event,
        )

    return app
