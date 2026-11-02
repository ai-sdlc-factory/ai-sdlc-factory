FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY tests/integration/DevFoundry.IntegrationTests/DevFoundry.IntegrationTests.csproj tests/integration/DevFoundry.IntegrationTests/
RUN dotnet restore tests/integration/DevFoundry.IntegrationTests
COPY tests/integration/DevFoundry.IntegrationTests tests/integration/DevFoundry.IntegrationTests
RUN dotnet build tests/integration/DevFoundry.IntegrationTests -c Release --no-restore
CMD ["dotnet", "test", "tests/integration/DevFoundry.IntegrationTests", "-c", "Release", "--no-build"]
