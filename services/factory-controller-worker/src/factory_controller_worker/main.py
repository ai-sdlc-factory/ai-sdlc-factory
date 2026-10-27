import asyncio
import logging

from temporalio.client import Client
from temporalio.worker import Worker

from .config import WorkerSettings
from .workflows import ForgejoEventWorkflow


async def main() -> None:
    settings = WorkerSettings()
    client = await Client.connect(
        settings.temporal_address, namespace=settings.temporal_namespace
    )
    worker = Worker(
        client,
        task_queue=settings.temporal_task_queue,
        workflows=[ForgejoEventWorkflow],
    )
    logging.getLogger(__name__).info(
        "Listening on Temporal task queue %s", settings.temporal_task_queue
    )
    await worker.run()


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO)
    asyncio.run(main())
