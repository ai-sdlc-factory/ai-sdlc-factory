# Sandbox image for .NET projects with a JavaScript/TypeScript front end: the .NET SDK and Node.js with npm, plus git and the OpenCode agent.
# It runs as an arbitrary unprivileged user on a read-only root filesystem, started by the sandbox
# launcher; nothing here is a service. Build context is sandboxes/ (shared entrypoint).
# Build: docker compose --profile images build sandbox-image-dotnet-node
FROM node:22-bookworm-slim AS node
FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_HOME=/tmp/dotnet-home NUGET_PACKAGES=/tmp/nuget \
    XDG_DATA_HOME=/tmp/data OPENCODE_CONFIG_DIR=/tmp/factory-config \
    NPM_CONFIG_CACHE=/tmp/npm-cache NPM_CONFIG_UPDATE_NOTIFIER=false NPM_CONFIG_FUND=false NPM_CONFIG_AUDIT=false \
    COREPACK_HOME=/tmp/corepack COREPACK_ENABLE_DOWNLOAD_PROMPT=0
# Node.js LTS with npm and corepack, copied from the official image (same version as the node sandbox).
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
    && ln -s ../lib/node_modules/corepack/dist/corepack.js /usr/local/bin/corepack \
    && corepack enable
# Pinned official Linux x64 baseline binary, verified against the release digest.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git ripgrep \
    && rm -rf /var/lib/apt/lists/* \
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
