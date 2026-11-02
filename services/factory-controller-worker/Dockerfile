FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props ./
COPY src/FactoryController.Worker/FactoryController.Worker.csproj src/FactoryController.Worker/
COPY tests/FactoryController.Worker.Tests/FactoryController.Worker.Tests.csproj tests/FactoryController.Worker.Tests/
RUN dotnet restore tests/FactoryController.Worker.Tests/FactoryController.Worker.Tests.csproj
COPY src ./src
COPY tests ./tests
RUN dotnet publish src/FactoryController.Worker -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/FactoryController.Worker.Tests -c Release --no-restore
CMD ["dotnet", "test", "tests/FactoryController.Worker.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 worker
COPY --from=build /out ./
USER worker
CMD ["dotnet", "FactoryController.Worker.dll"]
