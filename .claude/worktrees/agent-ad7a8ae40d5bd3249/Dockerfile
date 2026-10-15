# syntax=docker/dockerfile:1
# Web image: the web app plus its administrative commands (init-local, storage-init, migrate, seed, reset, probe, worker).
# Build from the repository root:  docker compose build web
# The same image runs locally and in AWS; behaviour differs only by configuration (deployability contract).

FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY data/reference/ data/reference/
COPY src/EventPermits.Domain/ src/EventPermits.Domain/
COPY src/EventPermits.Application/ src/EventPermits.Application/
COPY src/EventPermits.Infrastructure/ src/EventPermits.Infrastructure/
COPY src/EventPermits.Web/ src/EventPermits.Web/
RUN dotnet restore src/EventPermits.Web/EventPermits.Web.csproj --locked-mode
ARG SOURCE_REVISION=local
RUN dotnet publish src/EventPermits.Web/EventPermits.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false /p:SourceRevisionId=${SOURCE_REVISION}

# AWS RDS global CA bundle so the deploy track can connect with SSL Mode=VerifyFull (A-15). Checksum pinned.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS certs
ARG RDS_BUNDLE_SHA256=90a6f1a7978a1b1b096dca6c57511ca48d3b8cf052f8e704baf358f34ea2f21b
RUN curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /rds-global-bundle.pem \
 && echo "${RDS_BUNDLE_SHA256}  /rds-global-bundle.pem" | sha256sum -c -

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app
COPY --from=build /app .
COPY --from=certs /rds-global-bundle.pem /app/certs/rds-global-bundle.pem
RUN mkdir -p /bootstrap && chown $APP_UID:$APP_UID /bootstrap
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "EventPermits.Web.dll"]
