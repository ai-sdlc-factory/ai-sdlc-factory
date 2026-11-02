FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /tests
COPY FactoryController.IntegrationTests/FactoryController.IntegrationTests.csproj FactoryController.IntegrationTests/
RUN dotnet restore FactoryController.IntegrationTests
COPY FactoryController.IntegrationTests ./FactoryController.IntegrationTests
RUN dotnet build FactoryController.IntegrationTests -c Release --no-restore
CMD ["dotnet", "test", "FactoryController.IntegrationTests", "-c", "Release", "--no-build"]
