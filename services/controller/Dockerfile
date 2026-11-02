FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY libs/DevFoundry.Contracts/DevFoundry.Contracts.csproj libs/DevFoundry.Contracts/
COPY services/controller/src/DevFoundry.Controller/DevFoundry.Controller.csproj services/controller/src/DevFoundry.Controller/
COPY services/controller/tests/DevFoundry.Controller.Tests/DevFoundry.Controller.Tests.csproj services/controller/tests/DevFoundry.Controller.Tests/
RUN dotnet restore services/controller/tests/DevFoundry.Controller.Tests/DevFoundry.Controller.Tests.csproj
COPY libs ./libs
COPY services/controller ./services/controller
RUN dotnet publish services/controller/src/DevFoundry.Controller -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build services/controller/tests/DevFoundry.Controller.Tests -c Release --no-restore
CMD ["dotnet", "test", "services/controller/tests/DevFoundry.Controller.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 worker
COPY --from=build /out ./
USER worker
CMD ["dotnet", "DevFoundry.Controller.dll"]
