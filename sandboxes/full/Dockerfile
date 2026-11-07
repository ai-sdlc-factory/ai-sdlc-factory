# Sandbox image with everything: the .NET SDK, Python 3 (pytest and ruff) and Node.js with npm, plus git, ripgrep and the OpenCode agent. Large; prefer a smaller image when a project needs fewer toolchains.
# It runs as an arbitrary unprivileged user on a read-only root filesystem, started by the sandbox
# launcher; nothing here is a service. Build context is sandboxes/ (shared entrypoint).
# Build: docker compose --profile images build sandbox-image-full
FROM node:22-bookworm-slim AS node
FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_HOME=/tmp/dotnet-home NUGET_PACKAGES=/tmp/nuget \
    XDG_DATA_HOME=/tmp/data OPENCODE_CONFIG_DIR=/tmp/factory-config \
    PYTHONDONTWRITEBYTECODE=1 PYTHONPYCACHEPREFIX=/tmp/pycache \
    PYTEST_ADDOPTS="-p no:cacheprovider" PIP_CACHE_DIR=/tmp/pip-cache PIP_DISABLE_PIP_VERSION_CHECK=1 \
    NPM_CONFIG_CACHE=/tmp/npm-cache NPM_CONFIG_UPDATE_NOTIFIER=false NPM_CONFIG_FUND=false NPM_CONFIG_AUDIT=false \
    COREPACK_HOME=/tmp/corepack COREPACK_ENABLE_DOWNLOAD_PROMPT=0
# Node.js LTS with npm and corepack, copied from the official image (same version as the node sandbox).
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && ln -s ../lib/node_modules/corepack/dist/corepack.js /usr/local/bin/corepack \
    && corepack enable
# Python comes from the distribution (3.12 on Ubuntu 24.04); packages go through pip with the system-packages guard off, because the container is disposable.
# Pinned official Linux x64 baseline binary, verified against the release digest.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git ripgrep python3 python3-pip python3-venv \
    && rm -rf /var/lib/apt/lists/* \
    && pip install --no-cache-dir --break-system-packages pytest ruff \
    && ln -s /usr/bin/python3 /usr/local/bin/python \
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
