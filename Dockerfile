# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so the package layer is cached until a project file changes.
COPY Directory.Build.props .editorconfig ./
COPY src/Flowboard.Core/Flowboard.Core.csproj src/Flowboard.Core/
COPY src/Flowboard.Web/Flowboard.Web.csproj src/Flowboard.Web/
RUN dotnet restore src/Flowboard.Web/Flowboard.Web.csproj

COPY src/ src/
# No --no-restore here: the SDK adds Blazor's framework assets (_framework/blazor.web.js) only when it
# restores with the .razor files present, so the restore above just warms the package cache.
RUN dotnet publish src/Flowboard.Web/Flowboard.Web.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Database__MigrateOnStartup=true
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "Flowboard.Web.dll"]
