# Build from this folder:  docker build --target test -t alert-intake-test alert-intake   (or --target runtime)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/DevFoundry.AlertIntake/DevFoundry.AlertIntake.csproj src/DevFoundry.AlertIntake/
COPY tests/DevFoundry.AlertIntake.Tests/DevFoundry.AlertIntake.Tests.csproj tests/DevFoundry.AlertIntake.Tests/
RUN dotnet restore tests/DevFoundry.AlertIntake.Tests/DevFoundry.AlertIntake.Tests.csproj
COPY src ./src
COPY tests ./tests
RUN dotnet publish src/DevFoundry.AlertIntake -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/DevFoundry.AlertIntake.Tests -c Release --no-restore
CMD ["dotnet", "test", "tests/DevFoundry.AlertIntake.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8000 DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 intake
COPY --from=build /out ./
USER intake
EXPOSE 8000
CMD ["dotnet", "DevFoundry.AlertIntake.dll"]
