FROM python:3.12.16-slim-bookworm AS base
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 \
    XDG_DATA_HOME=/data XDG_CACHE_HOME=/tmp/cache \
    XDG_CONFIG_HOME=/tmp/config XDG_STATE_HOME=/tmp/state \
    OPENCODE_CONFIG=/etc/opencode/opencode.json
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git libstdc++6 \
    && rm -rf /var/lib/apt/lists/*
# Pinned official Linux x64 baseline binary, verified against the release digest.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && curl -fsSL --retry 3 https://github.com/anomalyco/opencode/releases/download/v1.19.4/opencode-linux-x64-baseline.tar.gz -o /tmp/opencode.tar.gz \
    && echo "1697ef40558d378bd2f72efa8fafe1bed1f0e664f09247b9357556b95dbc7cbc  /tmp/opencode.tar.gz" | sha256sum -c - \
    && mkdir /tmp/opencode-bin \
    && tar -xzf /tmp/opencode.tar.gz -C /tmp/opencode-bin \
    && install /tmp/opencode-bin/opencode /usr/local/bin/opencode \
    && rm -rf /tmp/opencode-bin /tmp/opencode.tar.gz
COPY pyproject.toml ./
COPY src ./src
COPY opencode.json /etc/opencode/opencode.json
RUN pip install --no-cache-dir . \
    && useradd --system --uid 10001 --create-home worker \
    && mkdir -p /data/opencode \
    && mkdir -p /tmp/cache /tmp/config /tmp/state \
    && chown -R worker:worker /data /tmp/cache /tmp/config /tmp/state \
    && chmod 700 /data/opencode

FROM base AS test
RUN pip install --no-cache-dir ".[test]" \
    && chown -R worker:worker /tmp/cache /tmp/config /tmp/state
COPY tests ./tests
USER worker
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]

FROM base AS runtime
USER worker
CMD ["python", "-m", "opencode_worker.main"]
