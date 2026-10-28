from datetime import timedelta
from typing import Any

from temporalio import workflow
from temporalio.common import RetryPolicy


@workflow.defn
class OpenCodeDemoWorkflow:
    @workflow.run
    async def run(self, task: dict[str, Any]) -> dict[str, Any]:
        return await workflow.execute_activity(
            "run_opencode_task",
            task,
            task_queue=task.get("activity_task_queue", "opencode-tasks"),
            start_to_close_timeout=timedelta(minutes=5),
            heartbeat_timeout=timedelta(seconds=30),
            # A model call can consume plan usage; do not repeat it automatically.
            retry_policy=RetryPolicy(maximum_attempts=1),
        )
