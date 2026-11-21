FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY services/egress-proxy/src/DevFoundry.EgressProxy/DevFoundry.EgressProxy.csproj services/egress-proxy/src/DevFoundry.EgressProxy/
COPY services/egress-proxy/tests/DevFoundry.EgressProxy.Tests/DevFoundry.EgressProxy.Tests.csproj services/egress-proxy/tests/DevFoundry.EgressProxy.Tests/
RUN dotnet restore services/egress-proxy/tests/DevFoundry.EgressProxy.Tests/DevFoundry.EgressProxy.Tests.csproj
COPY services/egress-proxy ./services/egress-proxy
RUN dotnet publish services/egress-proxy/src/DevFoundry.EgressProxy -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build services/egress-proxy/tests/DevFoundry.EgressProxy.Tests -c Release --no-restore
CMD ["dotnet", "test", "services/egress-proxy/tests/DevFoundry.EgressProxy.Tests", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 egress \
    && mkdir -p /var/log/egress && chown egress /var/log/egress
COPY --from=build /out ./
USER egress
EXPOSE 3128
CMD ["dotnet", "DevFoundry.EgressProxy.dll"]
