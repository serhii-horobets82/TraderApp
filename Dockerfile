# Background worker image: no HTTP port, no inbound traffic. Works unchanged on
# Railway, Render, Fly.io, Koyeb and plain `docker run`.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# NuGet.config pins the source to nuget.org. Without it, restore inherits any
# private feed configured on the build machine and fails with NU1301.
COPY NuGet.config ./
COPY TraderApp/TraderApp.csproj TraderApp/
RUN dotnet restore TraderApp/TraderApp.csproj

COPY . .
RUN dotnet publish TraderApp/TraderApp.csproj \
      -c Release \
      -o /app \
      --no-restore \
      --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# The app only talks out over HTTPS; it never needs root.
RUN useradd --create-home --shell /usr/sbin/nologin trader \
 && mkdir -p /app/logs \
 && chown -R trader:trader /app
USER trader

COPY --from=build --chown=trader:trader /app ./

# The container filesystem is ephemeral on most PaaS hosts, so these logs go
# away on redeploy. Platform logs and Telegram are the durable record.
ENV DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0

# No HEALTHCHECK: there is no port to probe, and "process alive" is already what
# the platform restarts on. Liveness that matters is the Telegram heartbeat.

# The app installs a SIGTERM handler and shuts down gracefully - cancelling
# resting entry orders and flushing Telegram. Give it room to finish.
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "TraderApp.dll"]
