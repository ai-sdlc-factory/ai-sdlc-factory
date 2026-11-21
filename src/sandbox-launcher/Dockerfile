FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/contracts/DevFoundry.Contracts.csproj src/contracts/
COPY src/sandbox-launcher/DevFoundry.SandboxLauncher.csproj src/sandbox-launcher/
COPY tests/sandbox-launcher/DevFoundry.SandboxLauncher.Tests.csproj tests/sandbox-launcher/
RUN dotnet restore tests/sandbox-launcher/DevFoundry.SandboxLauncher.Tests.csproj
COPY src/contracts ./src/contracts
COPY src/sandbox-launcher ./src/sandbox-launcher
COPY tests/sandbox-launcher ./tests/sandbox-launcher
RUN dotnet publish src/sandbox-launcher -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/sandbox-launcher -c Release --no-restore
# TEST_FILTER (for example "Category=Live") is passed to dotnet test when set.
CMD ["sh", "-c", "dotnet test tests/sandbox-launcher -c Release --no-build ${TEST_FILTER:+--filter \"$TEST_FILTER\"}"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
# Not root: the Docker socket is reached through a supplementary group added in Compose.
RUN useradd --system --uid 10001 launcher
COPY --from=build /out ./
USER launcher
CMD ["dotnet", "DevFoundry.SandboxLauncher.dll"]
