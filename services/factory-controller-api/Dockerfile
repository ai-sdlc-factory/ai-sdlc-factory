FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src/factory-controller-api
COPY Directory.Build.props /src/
COPY factory-controller-api/src/FactoryController.Api/FactoryController.Api.csproj src/FactoryController.Api/
COPY factory-controller-api/tests/FactoryController.Api.Tests/FactoryController.Api.Tests.csproj tests/FactoryController.Api.Tests/
RUN dotnet restore tests/FactoryController.Api.Tests/FactoryController.Api.Tests.csproj
COPY factory-controller-api/src ./src
COPY factory-controller-api/tests ./tests
RUN dotnet publish src/FactoryController.Api -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/FactoryController.Api.Tests -c Release --no-restore
CMD ["dotnet", "test", "tests/FactoryController.Api.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8000 DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 controller
COPY --from=build /out ./
USER controller
EXPOSE 8000
CMD ["dotnet", "FactoryController.Api.dll"]
