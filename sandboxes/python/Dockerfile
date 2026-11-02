# Sandbox image for Python projects: Python 3.12, git, ripgrep, pytest and ruff, plus the OpenCode agent.
# It runs as an arbitrary unprivileged user on a read-only root filesystem, started by the sandbox
# launcher; nothing here is a service. Build context is sandboxes/ (shared entrypoint).
# Build: docker compose --profile images build
FROM python:3.12-slim-bookworm
ENV XDG_DATA_HOME=/tmp/data OPENCODE_CONFIG_DIR=/tmp/factory-config \
    PYTHONDONTWRITEBYTECODE=1 PYTHONPYCACHEPREFIX=/tmp/pycache \
    PYTEST_ADDOPTS="-p no:cacheprovider" PIP_CACHE_DIR=/tmp/pip-cache PIP_DISABLE_PIP_VERSION_CHECK=1
# Pinned official Linux x64 baseline binary, verified against the release digest.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git ripgrep \
    && rm -rf /var/lib/apt/lists/* \
    && pip install --no-cache-dir pytest ruff \
    && curl -fsSL --retry 3 https://github.com/anomalyco/opencode/releases/download/v1.19.4/opencode-linux-x64-baseline.tar.gz -o /tmp/opencode.tar.gz \
    && echo "1697ef40558d378bd2f72efa8fafe1bed1f0e664f09247b9357556b95dbc7cbc  /tmp/opencode.tar.gz" | sha256sum -c - \
    && mkdir /tmp/opencode-bin \
    && tar -xzf /tmp/opencode.tar.gz -C /tmp/opencode-bin \
    && install /tmp/opencode-bin/opencode /usr/local/bin/opencode \
    && rm -rf /tmp/opencode-bin /tmp/opencode.tar.gz
# The workspace is owned by the host user; the launcher runs the container as that user.
# git would otherwise refuse a directory it considers owned by someone else.
RUN git config --system --add safe.directory /workspace
COPY --chmod=755 common/factory-entry.sh /usr/local/bin/factory-entry
WORKDIR /workspace
ENTRYPOINT ["factory-entry"]
