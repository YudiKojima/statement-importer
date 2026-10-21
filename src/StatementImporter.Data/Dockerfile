# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY dotnet-tools.json ./
RUN dotnet tool restore
COPY src/StatementImporter.Contracts/StatementImporter.Contracts.csproj src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/StatementImporter.Data.csproj src/StatementImporter.Data/
RUN dotnet restore src/StatementImporter.Data/StatementImporter.Data.csproj -r linux-x64

FROM restore AS publish
COPY src/StatementImporter.Contracts/ src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/ src/StatementImporter.Data/
RUN dotnet ef migrations bundle \
      --project src/StatementImporter.Data --startup-project src/StatementImporter.Data \
      --configuration Release --self-contained -r linux-x64 -o /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/YudiKojima/statement-importer" \
      org.opencontainers.image.title="statement-importer-migrator" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
WORKDIR /app
COPY --from=publish /out/efbundle ./
ENTRYPOINT ["/app/efbundle"]
