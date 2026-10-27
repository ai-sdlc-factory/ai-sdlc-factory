from dataclasses import dataclass
from typing import Any


@dataclass
class ForgejoEvent:
    delivery_id: str
    event: str
    payload: dict[str, Any]
