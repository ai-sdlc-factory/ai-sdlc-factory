import asyncio
import logging

from temporalio.client import Client
from temporalio.worker import Worker

from .activities import run_opencode_task
from .config import Settings


async def main() -> None:
    settings = Settings()
    client = await Client.connect(
        settings.temporal_address, namespace=settings.temporal_namespace
    )
    worker = Worker(
        client,
        task_queue=settings.opencode_task_queue,
        activities=[run_opencode_task],
        max_concurrent_activities=1,
    )
    logging.getLogger(__name__).info(
        "OpenCode activity worker listening on %s", settings.opencode_task_queue
    )
    await worker.run()


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO)
    asyncio.run(main())
