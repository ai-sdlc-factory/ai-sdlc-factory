from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class WorkerSettings(BaseSettings):
    model_config = SettingsConfigDict(extra="ignore", hide_input_in_errors=True)

    temporal_address: str = Field(default="localhost:7233", min_length=1)
    temporal_namespace: str = Field(default="default", min_length=1)
    temporal_task_queue: str = Field(default="factory-events", min_length=1)
