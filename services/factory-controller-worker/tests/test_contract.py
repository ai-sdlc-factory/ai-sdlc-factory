import asyncio
import importlib.util
import json

from temporalio.api.common.v1 import Payload
from temporalio.converter import DataConverter

from factory_controller_worker.models import ForgejoEvent
from factory_controller_worker.workflows import ForgejoEventWorkflow


def test_existing_json_contract_is_decoded_and_processed():
    """The API's plain JSON must also match histories created before the split."""
    data = {
        "delivery_id": "12345678-1234-1234-1234-123456789abc",
        "event": "issues",
        "payload": {
            "repository": {"full_name": "test/contract"},
            "action": "opened",
        },
    }
    payload = Payload(
        metadata={"encoding": b"json/plain"}, data=json.dumps(data).encode()
    )

    async def check():
        event, = await DataConverter.default.decode([payload], [ForgejoEvent])
        assert event.payload == data["payload"]
        result = await ForgejoEventWorkflow().run(event)
        assert result == {
            "delivery_id": data["delivery_id"],
            "event": "issues",
            "repository": "test/contract",
            "action": "opened",
            "ref": None,
        }

    asyncio.run(check())


def test_worker_does_not_install_the_api_or_http_framework():
    assert importlib.util.find_spec("factory_controller_api") is None
    assert importlib.util.find_spec("fastapi") is None
    assert importlib.util.find_spec("uvicorn") is None
