from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict


class TemporalSettings(BaseSettings):
    model_config = SettingsConfigDict(extra="ignore", hide_input_in_errors=True)

    temporal_address: str = Field(default="localhost:7233", min_length=1)
    temporal_namespace: str = Field(default="default", min_length=1)
    temporal_task_queue: str = Field(default="factory-events", min_length=1)


class ApiSettings(TemporalSettings):
    forgejo_webhook_secret: SecretStr = Field(min_length=32)
    max_webhook_bytes: int = Field(default=1_048_576, ge=1, le=1_048_576)
