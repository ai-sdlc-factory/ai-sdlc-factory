FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/contracts/AiSdlcFactory.Contracts.csproj src/contracts/
COPY src/api/AiSdlcFactory.Api.csproj src/api/
COPY tests/api/AiSdlcFactory.Api.Tests.csproj tests/api/
RUN dotnet restore tests/api/AiSdlcFactory.Api.Tests.csproj
COPY src/contracts ./src/contracts
COPY src/api ./src/api
COPY tests/api ./tests/api
RUN dotnet publish src/api -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/api -c Release --no-restore
CMD ["dotnet", "test", "tests/api", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8000 DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 controller
COPY --from=build /out ./
USER controller
EXPOSE 8000
CMD ["dotnet", "AiSdlcFactory.Api.dll"]
