# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/StatementImporter.Contracts/StatementImporter.Contracts.csproj src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/StatementImporter.Data.csproj src/StatementImporter.Data/
COPY src/StatementImporter.Worker/StatementImporter.Worker.csproj src/StatementImporter.Worker/
RUN dotnet restore src/StatementImporter.Worker/StatementImporter.Worker.csproj -r linux-x64

FROM restore AS build
COPY src/StatementImporter.Contracts/ src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/ src/StatementImporter.Data/
COPY src/StatementImporter.Worker/ src/StatementImporter.Worker/
RUN dotnet build src/StatementImporter.Worker/StatementImporter.Worker.csproj -c Release -r linux-x64 --no-restore

FROM build AS dev
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
WORKDIR /src/src/StatementImporter.Worker
ENTRYPOINT ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive", "-r", "linux-x64"]

FROM build AS publish
RUN dotnet publish src/StatementImporter.Worker/StatementImporter.Worker.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /out \
 && mkdir /empty

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/YudiKojima/statement-importer" \
      org.opencontainers.image.title="statement-importer-worker" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=publish --chown=1654:1654 /empty /data/uploads
COPY --from=publish /out ./
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 CMD ["/app/StatementImporter.Worker", "--healthcheck"]
ENTRYPOINT ["/app/StatementImporter.Worker"]
