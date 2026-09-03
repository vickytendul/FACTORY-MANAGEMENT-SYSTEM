FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .

RUN dotnet restore "FACTORY MANAGEMENT SYSTEM.csproj"
RUN dotnet publish "FACTORY MANAGEMENT SYSTEM.csproj" -c Release -o /app/publish

RUN ls -la /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

COPY --from=build /app/publish .

# WebApplication.CreateBuilder registers appsettings.json/appsettings.
# {Environment}.json with reloadOnChange: true, which sets up a
# FileSystemWatcher backed by Linux's inotify API. Render's container hit
# "The configured user limit (128) on the number of inotify instances has
# been reached" during CreateBuilder itself, crashing startup before the
# app ever reached app.Run(). Runtime-only (not set in the build stage,
# which doesn't run the published app) - switches .NET's config/file
# providers to polling instead of inotify-based watching, avoiding the
# limit entirely without any host-level sysctl/ulimit change (which Render
# doesn't allow us to make anyway).
ENV DOTNET_USE_POLLING_FILE_WATCHER=true

# No hard-coded ASPNETCORE_URLS here - Render assigns the actual port to
# listen on via the PORT environment variable at container start (it is
# not a fixed value across deploys/services), and Program.cs binds to
# whatever that is at runtime, falling back to 10000 for local/non-Render
# use. A hard-coded port here previously caused Render's port scan to time
# out, since the app was always bound to 10000 regardless of the port
# Render actually wanted to route traffic to.
EXPOSE 10000

ENTRYPOINT ["dotnet", "FACTORY MANAGEMENT SYSTEM.dll"]