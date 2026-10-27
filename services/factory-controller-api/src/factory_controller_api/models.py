from typing import Literal

from pydantic import BaseModel


class WebhookReceipt(BaseModel):
    status: Literal["accepted", "duplicate"]
    workflow_id: str
    delivery_id: str
    event: str
