# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/StatementImporter.Web/StatementImporter.Web.csproj src/StatementImporter.Web/
RUN dotnet restore src/StatementImporter.Web/StatementImporter.Web.csproj

FROM restore AS publish
COPY src/StatementImporter.Contracts/Http/Responses.cs src/StatementImporter.Contracts/Http/
COPY src/StatementImporter.Contracts/Statuses.cs src/StatementImporter.Contracts/
COPY src/StatementImporter.Web/ src/StatementImporter.Web/
# O nginx da imagem oficial não tem o módulo brotli; só os .gz são servidos (gzip_static).
RUN dotnet publish src/StatementImporter.Web/StatementImporter.Web.csproj -c Release --no-restore -o /out \
 && find /out/wwwroot -name '*.br' -delete

FROM nginxinc/nginx-unprivileged:1.30-alpine AS runtime
# A tag da base não é reconstruída a cada correção do Alpine (ex.: brotli-libs com CVE CRITICAL);
# o upgrade traz os pacotes corrigidos sem esperar a imagem oficial.
USER root
RUN apk upgrade --no-cache
USER 101
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/YudiKojima/statement-importer" \
      org.opencontainers.image.title="statement-importer-proxy" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=publish /out/wwwroot /usr/share/nginx/html
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --start-interval=1s --retries=3 CMD ["wget", "-q", "--spider", "http://127.0.0.1:8080/"]
