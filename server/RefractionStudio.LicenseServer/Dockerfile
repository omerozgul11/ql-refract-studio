# syntax=docker/dockerfile:1
# Build context: the repository root, e.g.
#   docker build -f server/RefractionStudio.LicenseServer/Dockerfile -t quakelogic/refraction-license-server:1.0.0 .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Only what this service needs (global.json is left out so the image's SDK is used).
COPY Directory.Build.props Directory.Packages.props ./
COPY src/RefractionStudio.Licensing/ src/RefractionStudio.Licensing/
COPY server/RefractionStudio.Server.Data/ server/RefractionStudio.Server.Data/
COPY server/RefractionStudio.LicenseServer/ server/RefractionStudio.LicenseServer/
RUN dotnet restore server/RefractionStudio.LicenseServer/RefractionStudio.LicenseServer.csproj
RUN dotnet publish server/RefractionStudio.LicenseServer/RefractionStudio.LicenseServer.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
COPY --from=build /app ./
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "RefractionStudio.LicenseServer.dll", "--health-check"]
ENTRYPOINT ["dotnet", "RefractionStudio.LicenseServer.dll"]
