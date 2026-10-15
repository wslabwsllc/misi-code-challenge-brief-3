# syntax=docker/dockerfile:1
# Fictional external services (registry, calendar, notify, weather, geocode, partner-feed) in one image.
# Build context is the repository root (it needs data/reference/):
#   docker build -f src/EventPermits.Mocks/Dockerfile -t event-permits/mocks:local .
# Run one service per container, e.g.:
#   docker run -p 8080:8080 -e Mock__AdminKey=... event-permits/mocks:local --service registry
# Health check (no curl in the runtime image):
#   dotnet /app/EventPermits.Mocks.dll probe http://localhost:8080/health

FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/EventPermits.Mocks/EventPermits.Mocks.csproj src/EventPermits.Mocks/packages.lock.json src/EventPermits.Mocks/
RUN dotnet restore src/EventPermits.Mocks/EventPermits.Mocks.csproj --locked-mode
COPY data/reference/ data/reference/
COPY src/EventPermits.Mocks/ src/EventPermits.Mocks/
RUN dotnet publish src/EventPermits.Mocks/EventPermits.Mocks.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "EventPermits.Mocks.dll"]
