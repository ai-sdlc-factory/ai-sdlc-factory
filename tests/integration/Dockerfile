FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /tests
COPY DevFoundry.IntegrationTests/DevFoundry.IntegrationTests.csproj DevFoundry.IntegrationTests/
RUN dotnet restore DevFoundry.IntegrationTests
COPY DevFoundry.IntegrationTests ./DevFoundry.IntegrationTests
RUN dotnet build DevFoundry.IntegrationTests -c Release --no-restore
CMD ["dotnet", "test", "DevFoundry.IntegrationTests", "-c", "Release", "--no-build"]
