import asyncio
from typing import Any

from temporalio import activity

from .config import Settings
from .runner import execute_task


@activity.defn(name="run_opencode_task")
async def run_opencode_task(task: dict[str, Any]) -> dict[str, Any]:
    async def heartbeat() -> None:
        while True:
            activity.heartbeat("OpenCode execution is active")
            await asyncio.sleep(5)

    pulse = asyncio.create_task(heartbeat())
    try:
        return await execute_task(task, Settings())
    finally:
        pulse.cancel()
        await asyncio.gather(pulse, return_exceptions=True)
