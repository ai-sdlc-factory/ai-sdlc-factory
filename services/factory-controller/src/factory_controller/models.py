from dataclasses import dataclass
from typing import Any, Literal

from pydantic import BaseModel


@dataclass
class ForgejoEvent:
    delivery_id: str
    event: str
    payload: dict[str, Any]


class WebhookReceipt(BaseModel):
    status: Literal["accepted", "duplicate"]
    workflow_id: str
    delivery_id: str
    event: str
