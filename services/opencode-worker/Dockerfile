FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props ./
COPY src/OpenCodeWorker/OpenCodeWorker.csproj src/OpenCodeWorker/
COPY tests/OpenCodeWorker.Tests/OpenCodeWorker.Tests.csproj tests/OpenCodeWorker.Tests/
RUN dotnet restore tests/OpenCodeWorker.Tests/OpenCodeWorker.Tests.csproj
COPY src ./src
COPY tests ./tests
RUN dotnet publish src/OpenCodeWorker -c Release --no-restore -o /out

# The tests use shell-script stand-ins for the OpenCode binary, not the real model.
FROM build AS test
RUN dotnet build tests/OpenCodeWorker.Tests -c Release --no-restore
CMD ["dotnet", "test", "tests/OpenCodeWorker.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0 \
    XDG_DATA_HOME=/data XDG_CACHE_HOME=/tmp/cache \
    XDG_CONFIG_HOME=/tmp/config XDG_STATE_HOME=/tmp/state \
    OPENCODE_CONFIG=/etc/opencode/opencode.json
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git libstdc++6 util-linux \
    && rm -rf /var/lib/apt/lists/*
# Pinned official Linux x64 baseline binary, verified against the release digest.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && curl -fsSL --retry 3 https://github.com/anomalyco/opencode/releases/download/v1.19.4/opencode-linux-x64-baseline.tar.gz -o /tmp/opencode.tar.gz \
    && echo "1697ef40558d378bd2f72efa8fafe1bed1f0e664f09247b9357556b95dbc7cbc  /tmp/opencode.tar.gz" | sha256sum -c - \
    && mkdir /tmp/opencode-bin \
    && tar -xzf /tmp/opencode.tar.gz -C /tmp/opencode-bin \
    && install /tmp/opencode-bin/opencode /usr/local/bin/opencode \
    && rm -rf /tmp/opencode-bin /tmp/opencode.tar.gz
COPY opencode.json /etc/opencode/opencode.json
# Same UID as the earlier image, so the existing opencode-data volume stays readable.
RUN useradd --system --uid 10001 --create-home worker \
    && mkdir -p /data/opencode /tmp/cache /tmp/config /tmp/state \
    && chown -R worker:worker /data /tmp/cache /tmp/config /tmp/state \
    && chmod 700 /data/opencode
COPY --from=build /out ./
USER worker
CMD ["dotnet", "OpenCodeWorker.dll"]
