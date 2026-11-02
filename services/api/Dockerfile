FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src/api
COPY Directory.Build.props /src/
COPY api/src/DevFoundry.Api/DevFoundry.Api.csproj src/DevFoundry.Api/
COPY api/tests/DevFoundry.Api.Tests/DevFoundry.Api.Tests.csproj tests/DevFoundry.Api.Tests/
RUN dotnet restore tests/DevFoundry.Api.Tests/DevFoundry.Api.Tests.csproj
COPY api/src ./src
COPY api/tests ./tests
RUN dotnet publish src/DevFoundry.Api -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/DevFoundry.Api.Tests -c Release --no-restore
CMD ["dotnet", "test", "tests/DevFoundry.Api.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8000 DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 controller
COPY --from=build /out ./
USER controller
EXPOSE 8000
CMD ["dotnet", "DevFoundry.Api.dll"]
