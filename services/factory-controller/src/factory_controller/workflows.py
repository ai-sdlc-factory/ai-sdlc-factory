from typing import Any

from temporalio import workflow

with workflow.unsafe.imports_passed_through():
    from .models import ForgejoEvent


@workflow.defn
class ForgejoEventWorkflow:
    @workflow.run
    async def run(self, event: ForgejoEvent) -> dict[str, Any]:
        """Preserve the input in workflow history and produce a small receipt.

        Factory actions will be added here once their behavior is defined.
        Webhook contents are data; this workflow executes no supplied commands.
        """
        repository = event.payload.get("repository")
        repository_name = (
            repository.get("full_name") if isinstance(repository, dict) else None
        )
        return {
            "delivery_id": event.delivery_id,
            "event": event.event,
            "repository": repository_name if isinstance(repository_name, str) else None,
            "action": event.payload.get("action"),
            "ref": event.payload.get("ref"),
        }
