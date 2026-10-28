from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(extra="ignore", hide_input_in_errors=True)

    temporal_address: str = "localhost:7233"
    temporal_namespace: str = "default"
    temporal_task_queue: str = "factory-events"
    opencode_task_queue: str = "opencode-tasks"
    opencode_model: str = Field(default="openai/gpt-6.1-sol", pattern=r"^openai/[^\s]+$")
    opencode_timeout_seconds: int = Field(default=240, ge=1, le=240)
    opencode_data_home: Path = Path("/data")
