"""Temporal routing test; the OpenCode subprocess is explicitly a stub."""
import asyncio
import os
from pathlib import Path
from uuid import uuid4

import pytest
from temporalio import activity
from temporalio.client import Client
from temporalio.worker import Worker

from opencode_worker.config import Settings
from opencode_worker.runner import execute_task


@pytest.mark.skipif(
    not os.environ.get("TEMPORAL_TEST_ADDRESS"),
    reason="Set TEMPORAL_TEST_ADDRESS to test the deployed controller workflow",
)
def test_demo_routes_to_execution_worker(tmp_path):
    asyncio.run(run_demo(tmp_path))


async def run_demo(tmp_path: Path):
    queue = f"opencode-test-{uuid4()}"
    task_id = str(uuid4())
    data = tmp_path / "data"
    (data / "opencode").mkdir(parents=True)
    (data / "opencode/auth.json").write_text("{}")
    binary = tmp_path / "fake-opencode"
    binary.write_text(
        "#!/usr/local/bin/python\n"
        "from pathlib import Path\n"
        "Path('hello.py').write_text('def greet(name): return name')\n"
        "Path('test_hello.py').write_text('# demo test')\n"
        "print('{}')\n"
    )
    binary.chmod(0o755)
    settings = Settings(opencode_data_home=data)

    @activity.defn(name="run_opencode_task")
    async def run_task(task: dict) -> dict:
        return await execute_task(task, settings, str(binary))

    client = await Client.connect(
        os.environ["TEMPORAL_TEST_ADDRESS"],
        namespace=os.environ.get("TEMPORAL_TEST_NAMESPACE", "default"),
    )
    async with Worker(client, task_queue=queue, activities=[run_task]):
        result = await asyncio.wait_for(
            client.execute_workflow(
                "OpenCodeDemoWorkflow",
                {"task_id": task_id, "prompt": "Create demo files.", "activity_task_queue": queue},
                id=f"opencode-test-{task_id}",
                task_queue=os.environ.get("TEMPORAL_TASK_QUEUE", "factory-events"),
            ),
            timeout=30,
        )
    assert result["task_id"] == task_id
    assert set(result["artifacts"]) == {"hello.py", "test_hello.py"}
