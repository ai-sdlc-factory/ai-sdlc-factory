FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY libs/DevFoundry.Contracts/DevFoundry.Contracts.csproj libs/DevFoundry.Contracts/
COPY services/sandbox-launcher/src/DevFoundry.SandboxLauncher/DevFoundry.SandboxLauncher.csproj services/sandbox-launcher/src/DevFoundry.SandboxLauncher/
COPY services/sandbox-launcher/tests/DevFoundry.SandboxLauncher.Tests/DevFoundry.SandboxLauncher.Tests.csproj services/sandbox-launcher/tests/DevFoundry.SandboxLauncher.Tests/
RUN dotnet restore services/sandbox-launcher/tests/DevFoundry.SandboxLauncher.Tests/DevFoundry.SandboxLauncher.Tests.csproj
COPY libs ./libs
COPY services/sandbox-launcher ./services/sandbox-launcher
RUN dotnet publish services/sandbox-launcher/src/DevFoundry.SandboxLauncher -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build services/sandbox-launcher/tests/DevFoundry.SandboxLauncher.Tests -c Release --no-restore
CMD ["dotnet", "test", "services/sandbox-launcher/tests/DevFoundry.SandboxLauncher.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
# Not root: the Docker socket is reached through a supplementary group added in Compose.
RUN useradd --system --uid 10001 launcher
COPY --from=build /out ./
USER launcher
CMD ["dotnet", "DevFoundry.SandboxLauncher.dll"]
