import asyncio
import json
from datetime import timedelta
from uuid import uuid4

from temporalio.client import Client

from .config import Settings

DEMO_PROMPT = (
    "Create exactly two files in the current workspace: hello.py and test_hello.py. "
    "hello.py must define greet(name: str) -> str returning 'Hello, ' plus the name plus '!'. "
    "test_hello.py must contain Python unittest tests for greet('Factory') and greet('World'). "
    "Use file editing tools. Do not run commands, access external directories, or use network tools. "
    "Finish with a short description of the two files."
)


async def main() -> None:
    settings = Settings()
    client = await Client.connect(
        settings.temporal_address, namespace=settings.temporal_namespace
    )
    task_id = str(uuid4())
    handle = await client.start_workflow(
        "OpenCodeDemoWorkflow",
        {
            "task_id": task_id,
            "prompt": DEMO_PROMPT,
            "activity_task_queue": settings.opencode_task_queue,
        },
        id=f"opencode-demo-{task_id}",
        task_queue=settings.temporal_task_queue,
        execution_timeout=timedelta(minutes=6),
    )
    print(f"Workflow: {handle.id}", flush=True)
    result = await asyncio.wait_for(handle.result(), timeout=330)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    asyncio.run(main())
