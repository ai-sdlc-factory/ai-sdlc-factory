import asyncio
import logging
import signal
from pathlib import Path

from temporalio.client import Client
from temporalio.worker import Worker

from .config import WorkerSettings
from .demo_workflow import OpenCodeDemoWorkflow
from .workflows import ForgejoEventWorkflow

# Present while the worker is polling; the container healthcheck tests for it.
READY_FILE = Path("/tmp/worker-ready")


async def main() -> None:
    settings = WorkerSettings()
    client = await Client.connect(
        settings.temporal_address, namespace=settings.temporal_namespace
    )
    worker = Worker(
        client,
        task_queue=settings.temporal_task_queue,
        workflows=[ForgejoEventWorkflow, OpenCodeDemoWorkflow],
    )
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)

    async with worker:
        READY_FILE.touch()
        logging.getLogger(__name__).info(
            "Listening on Temporal task queue %s", settings.temporal_task_queue
        )
        try:
            await stop.wait()
        finally:
            READY_FILE.unlink(missing_ok=True)


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO)
    asyncio.run(main())
