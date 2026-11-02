FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src/controller
COPY Directory.Build.props /src/
COPY controller/src/DevFoundry.Controller/DevFoundry.Controller.csproj src/DevFoundry.Controller/
COPY controller/tests/DevFoundry.Controller.Tests/DevFoundry.Controller.Tests.csproj tests/DevFoundry.Controller.Tests/
RUN dotnet restore tests/DevFoundry.Controller.Tests/DevFoundry.Controller.Tests.csproj
COPY controller/src ./src
COPY controller/tests ./tests
RUN dotnet publish src/DevFoundry.Controller -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/DevFoundry.Controller.Tests -c Release --no-restore
CMD ["dotnet", "test", "tests/DevFoundry.Controller.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 worker
COPY --from=build /out ./
USER worker
CMD ["dotnet", "DevFoundry.Controller.dll"]
