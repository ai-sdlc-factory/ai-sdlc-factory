FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/egress-proxy/AiSdlcFactory.EgressProxy.csproj src/egress-proxy/
COPY tests/egress-proxy/AiSdlcFactory.EgressProxy.Tests.csproj tests/egress-proxy/
RUN dotnet restore tests/egress-proxy/AiSdlcFactory.EgressProxy.Tests.csproj
COPY src/egress-proxy ./src/egress-proxy
COPY tests/egress-proxy ./tests/egress-proxy
RUN dotnet publish src/egress-proxy -c Release --no-restore -o /out

FROM build AS test
RUN dotnet build tests/egress-proxy -c Release --no-restore
CMD ["dotnet", "test", "tests/egress-proxy", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ENV DOTNET_EnableDiagnostics=0
WORKDIR /app
RUN useradd --system --uid 10001 egress \
    && mkdir -p /var/log/egress && chown egress /var/log/egress
COPY --from=build /out ./
USER egress
EXPOSE 3128
CMD ["dotnet", "AiSdlcFactory.EgressProxy.dll"]
