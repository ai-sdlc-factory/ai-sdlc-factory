# Sandbox image for JavaScript/TypeScript projects: Node.js LTS with npm, git, ripgrep, plus the OpenCode agent.
# It runs as an arbitrary unprivileged user on a read-only root filesystem, started by the sandbox
# launcher; nothing here is a service. Build context is sandboxes/ (shared entrypoint).
# Build: docker compose --profile images build
# Projects bring their own dependencies (npm install / npm ci work through the egress proxy's
# package registries); Node's built-in test runner (`node --test`) needs nothing extra.
FROM node:22-bookworm-slim
ENV XDG_DATA_HOME=/tmp/data OPENCODE_CONFIG_DIR=/tmp/factory-config \
    NPM_CONFIG_CACHE=/tmp/npm-cache NPM_CONFIG_UPDATE_NOTIFIER=false NPM_CONFIG_FUND=false NPM_CONFIG_AUDIT=false \
    COREPACK_HOME=/tmp/corepack COREPACK_ENABLE_DOWNLOAD_PROMPT=0 NODE_ENV=development
# Pinned official Linux x64 baseline binary, verified against the release digest.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git ripgrep \
    && rm -rf /var/lib/apt/lists/* \
    && corepack enable \
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
