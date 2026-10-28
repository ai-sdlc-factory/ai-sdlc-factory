import asyncio
import json
import os
import re
import signal
import tempfile
from pathlib import Path
from typing import Any

from temporalio.exceptions import ApplicationError

from .config import Settings

ARTIFACT_NAMES = ("hello.py", "test_hello.py")
STREAM_LIMIT = 131_072
ARTIFACT_LIMIT = 16_384


async def read_limited(stream: asyncio.StreamReader) -> bytes:
    """Drain the pipe without retaining unbounded model or CLI output."""
    output = bytearray()
    while chunk := await stream.read(4096):
        if len(output) < STREAM_LIMIT:
            output.extend(chunk[: STREAM_LIMIT - len(output)])
    return bytes(output)


def assistant_text(output: bytes) -> str:
    messages = []
    for line in output.splitlines():
        try:
            event = json.loads(line)
        except (ValueError, UnicodeDecodeError):
            continue
        if not isinstance(event, dict):
            continue
        if event.get("type") == "error":
            raise ApplicationError(
                "OpenCode reported an error; check the ChatGPT login and selected model.",
                non_retryable=True,
            )
        part = event.get("part", {})
        if event.get("type") == "text" and isinstance(part, dict):
            text = part.get("text")
            if isinstance(text, str):
                messages.append(text)
    return "\n".join(messages)[:16_384]


def auth_values(path: Path) -> list[str]:
    try:
        data = json.loads(path.read_text())
    except (OSError, ValueError):
        return []
    values = []
    def collect(item):
        if isinstance(item, dict):
            for value in item.values():
                collect(value)
        elif isinstance(item, list):
            for value in item:
                collect(value)
        elif isinstance(item, str) and len(item) > 10:
            values.append(item)
    collect(data)
    return values


def failure_detail(stderr: bytes, secrets: list[str]) -> str:
    text = stderr.decode("utf-8", errors="replace")
    for secret in secrets:
        text = text.replace(secret, "[REDACTED]")
    text = re.sub(r"Bearer\s+[^\s\\\"]+", "Bearer [REDACTED]", text)
    lines = [line for line in text.splitlines() if "ERROR" in line or "Error:" in line]
    return (lines[-1] if lines else "Check the ChatGPT login and selected model.")[:1800]


async def execute_task(
    task: dict[str, Any], settings: Settings, executable: str = "opencode"
) -> dict[str, Any]:
    prompt = task.get("prompt")
    task_id = task.get("task_id")
    if not isinstance(prompt, str) or not 1 <= len(prompt) <= 8000:
        raise ApplicationError("Task prompt must contain 1 to 8000 characters.", non_retryable=True)
    if not isinstance(task_id, str) or not 1 <= len(task_id) <= 100:
        raise ApplicationError("Task ID is missing or invalid.", non_retryable=True)

    auth = settings.opencode_data_home / "opencode" / "auth.json"
    if not auth.is_file():
        raise ApplicationError(
            "OpenCode is not signed in. Run the opencode-login Compose service first.",
            non_retryable=True,
        )

    secrets = auth_values(auth)
    with tempfile.TemporaryDirectory(prefix="opencode-task-") as temporary:
        workspace = Path(temporary)
        # Explicit environment: no database passwords, webhook secret, or Temporal settings.
        child_env = {
            key: os.environ[key]
            for key in ("PATH", "HOME", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR")
            if key in os.environ
        }
        child_env.update({
            "XDG_DATA_HOME": str(settings.opencode_data_home),
            "XDG_CACHE_HOME": str(workspace / ".cache"),
            "XDG_CONFIG_HOME": str(workspace / ".config"),
            "XDG_STATE_HOME": str(workspace / ".state"),
            "OPENCODE_CONFIG": "/etc/opencode/opencode.json",
            "CI": "true",
        })
        process = await asyncio.create_subprocess_exec(
            executable, "--print-logs", "run", "--format", "json", "--model",
            settings.opencode_model, "--", prompt,
            cwd=workspace,
            env=child_env,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            start_new_session=True,
        )
        stdout = asyncio.create_task(read_limited(process.stdout))
        stderr = asyncio.create_task(read_limited(process.stderr))
        try:
            await asyncio.wait_for(process.wait(), settings.opencode_timeout_seconds)
        except TimeoutError:
            raise ApplicationError("OpenCode task timed out.", non_retryable=True) from None
        finally:
            # Kill any descendants too, including on Temporal cancellation.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            await process.wait()
            output, errors = await asyncio.gather(stdout, stderr)

        if process.returncode != 0:
            raise ApplicationError(
                f"OpenCode exited with code {process.returncode}: "
                + failure_detail(errors, secrets + auth_values(auth)),
                non_retryable=True,
            )
        text = assistant_text(output)
        artifacts = {}
        for name in ARTIFACT_NAMES:
            path = workspace / name
            if path.is_symlink() or not path.is_file():
                raise ApplicationError(f"OpenCode did not create a regular {name} file.", non_retryable=True)
            if path.stat().st_size > ARTIFACT_LIMIT:
                raise ApplicationError(f"Artifact {name} is too large.", non_retryable=True)
            try:
                artifacts[name] = path.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                raise ApplicationError(f"Artifact {name} is not UTF-8 text.", non_retryable=True) from None
        return {
            "task_id": task_id,
            "model": settings.opencode_model,
            "summary": text,
            "artifacts": artifacts,
        }
