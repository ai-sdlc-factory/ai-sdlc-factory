FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY libs/DevFoundry.Contracts/DevFoundry.Contracts.csproj libs/DevFoundry.Contracts/
COPY services/alert-intake/src/DevFoundry.AlertIntake/DevFoundry.AlertIntake.csproj services/alert-intake/src/DevFoundry.AlertIntake/
COPY services/alert-intake/tests/DevFoundry.AlertIntake.Tests/DevFoundry.AlertIntake.Tests.csproj services/alert-intake/tests/DevFoundry.AlertIntake.Tests/
RUN dotnet restore services/alert-intake/tests/DevFoundry.AlertIntake.Tests/DevFoundry.AlertIntake.Tests.csproj
COPY libs ./libs
COPY services/alert-intake ./services/alert-intake
RUN dotnet publish services/alert-intake/src/DevFoundry.AlertIntake -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build services/alert-intake/tests/DevFoundry.AlertIntake.Tests -c Release --no-restore
CMD ["dotnet", "test", "services/alert-intake/tests/DevFoundry.AlertIntake.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8000 DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 intake
COPY --from=build /out ./
USER intake
EXPOSE 8000
CMD ["dotnet", "DevFoundry.AlertIntake.dll"]
