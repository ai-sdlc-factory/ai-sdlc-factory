import asyncio

import pytest
from temporalio.exceptions import ApplicationError

from opencode_worker.config import Settings
from opencode_worker.runner import execute_task

TASK = {"task_id": "test-task", "prompt": "Create the demo files."}


def prepare(tmp_path, source):
    data = tmp_path / "data"
    (data / "opencode").mkdir(parents=True)
    (data / "opencode/auth.json").write_text("{}")
    binary = tmp_path / "fake-opencode"
    binary.write_text("#!/usr/local/bin/python\n" + source)
    binary.chmod(0o755)
    return Settings(opencode_data_home=data), str(binary)


def test_runs_without_shell_and_excludes_control_plane_secrets(tmp_path, monkeypatch):
    source = '''
import json,os,sys
from pathlib import Path
assert sys.argv[1:6] == ["--print-logs", "run", "--format", "json", "--model"]
assert sys.argv[-1] == "Create the demo files."
assert "TEMPORAL_ADDRESS" not in os.environ
assert "FORGEJO_WEBHOOK_SECRET" not in os.environ
assert "POSTGRES_PASSWORD" not in os.environ
Path("hello.py").write_text("def greet(name): return 'Hello, ' + name + '!'")
Path("test_hello.py").write_text("# example test")
print(json.dumps({"type":"text","part":{"text":"Created both files."}}))
'''
    settings, binary = prepare(tmp_path, source)
    monkeypatch.setenv("FORGEJO_WEBHOOK_SECRET", "must-not-be-forwarded")
    monkeypatch.setenv("POSTGRES_PASSWORD", "must-not-be-forwarded")
    result = asyncio.run(execute_task(TASK, settings, binary))
    assert result["summary"] == "Created both files."
    assert set(result["artifacts"]) == {"hello.py", "test_hello.py"}
    assert result["task_id"] == TASK["task_id"]


def test_requires_login_before_launching_opencode(tmp_path):
    with pytest.raises(ApplicationError, match="not signed in"):
        asyncio.run(execute_task(TASK, Settings(opencode_data_home=tmp_path), "missing"))


def test_timeout_stops_the_subprocess(tmp_path):
    settings, binary = prepare(tmp_path, "import time\ntime.sleep(30)\n")
    settings.opencode_timeout_seconds = 1
    with pytest.raises(ApplicationError, match="timed out"):
        asyncio.run(execute_task(TASK, settings, binary))


def test_error_event_is_not_success(tmp_path):
    settings, binary = prepare(tmp_path, 'print(\'{"type":"error"}\')\n')
    with pytest.raises(ApplicationError, match="reported an error"):
        asyncio.run(execute_task(TASK, settings, binary))


def test_artifact_symlink_is_rejected(tmp_path):
    source = 'from pathlib import Path\nPath("hello.py").symlink_to("/etc/passwd")\nPath("test_hello.py").write_text("# test")\n'
    settings, binary = prepare(tmp_path, source)
    with pytest.raises(ApplicationError, match="regular hello.py"):
        asyncio.run(execute_task(TASK, settings, binary))


def test_missing_artifacts_fail_the_task(tmp_path):
    settings, binary = prepare(tmp_path, 'print("{}")\n')
    with pytest.raises(ApplicationError, match="did not create"):
        asyncio.run(execute_task(TASK, settings, binary))

def test_failure_diagnostics_redact_credentials(tmp_path):
    token = "test-private-credential-value"
    source = "import sys\nprint('ERROR startup failure test-private-credential-value',file=sys.stderr)\nsys.exit(1)\n"
    settings, binary = prepare(tmp_path, source)
    (settings.opencode_data_home / "opencode/auth.json").write_text(
        '{"openai": {"access": "' + token + '"}}'
    )
    with pytest.raises(ApplicationError) as error:
        asyncio.run(execute_task(TASK, settings, binary))
    assert token not in str(error.value)
    assert "[REDACTED]" in str(error.value)
    assert "startup failure" in str(error.value)
