FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY libs/DevFoundry.Contracts/DevFoundry.Contracts.csproj libs/DevFoundry.Contracts/
COPY services/api/src/DevFoundry.Api/DevFoundry.Api.csproj services/api/src/DevFoundry.Api/
COPY services/api/tests/DevFoundry.Api.Tests/DevFoundry.Api.Tests.csproj services/api/tests/DevFoundry.Api.Tests/
RUN dotnet restore services/api/tests/DevFoundry.Api.Tests/DevFoundry.Api.Tests.csproj
COPY libs ./libs
COPY services/api ./services/api
RUN dotnet publish services/api/src/DevFoundry.Api -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build services/api/tests/DevFoundry.Api.Tests -c Release --no-restore
CMD ["dotnet", "test", "services/api/tests/DevFoundry.Api.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8000 DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 controller
COPY --from=build /out ./
USER controller
EXPOSE 8000
CMD ["dotnet", "DevFoundry.Api.dll"]
