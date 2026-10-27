# syntax=docker/dockerfile:1

# --- Frontend: Vue bundle into src/SocklessMail.Api/wwwroot/{js,css} -----------------------
FROM node:22-alpine AS frontend
WORKDIR /src/src/frontend
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
# vite.config.js takes the app version from the manifest.
COPY appinfo/info.xml /src/appinfo/info.xml
COPY src/frontend/ ./
RUN npm run build

# --- Backend: framework-dependent publish ---------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend
WORKDIR /src
COPY Directory.Build.props ./
COPY src/SocklessMail.Api/SocklessMail.Api.csproj src/SocklessMail.Api/
RUN dotnet restore src/SocklessMail.Api/SocklessMail.Api.csproj
COPY src/SocklessMail.Api/ src/SocklessMail.Api/
COPY --from=frontend /src/src/SocklessMail.Api/wwwroot/ src/SocklessMail.Api/wwwroot/
RUN dotnet publish src/SocklessMail.Api/SocklessMail.Api.csproj -c Release --no-restore -o /app -p:UseAppHost=false

# --- FRP client for the HaRP tunnel, checksum-pinned (developer manual, "Adapting ExApps to HaRP")
FROM alpine:3.22 AS frp
ARG FRP_VERSION=0.71.1
ARG FRP_AMD64_SHA256=1d9875210972a5d1a62b2dc4c48df45ab0af05d3ca1b5e494a014428259df803
ARG FRP_ARM64_SHA256=8edf590b0c396fa42f9c147121a76af83cfc2002b08f5aef0ea1008c43482019
RUN set -eux; \
    apk add --no-cache curl; \
    case "$(uname -m)" in \
      aarch64) arch=arm64; sum="$FRP_ARM64_SHA256" ;; \
      x86_64) arch=amd64; sum="$FRP_AMD64_SHA256" ;; \
      *) echo "unsupported architecture $(uname -m)"; exit 1 ;; \
    esac; \
    dir="frp_${FRP_VERSION}_linux_${arch}"; \
    curl -fsSL "https://github.com/fatedier/frp/releases/download/v${FRP_VERSION}/${dir}.tar.gz" -o /tmp/frp.tar.gz; \
    echo "${sum}  /tmp/frp.tar.gz" | sha256sum -c -; \
    tar -C /tmp -xzf /tmp/frp.tar.gz; \
    install -m 0755 "/tmp/${dir}/frpc" /usr/local/bin/frpc

# --- Runtime ----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# curl: healthcheck. su-exec: drop from root to the service user in entrypoint.sh.
# ca-certificates: AppAPI runs update-ca-certificates to install Nextcloud's trusted CAs.
RUN apk add --no-cache curl su-exec ca-certificates \
    && adduser -D -H -s /sbin/nologin sockless \
    && touch /frpc.toml && chown sockless:sockless /frpc.toml && chmod 600 /frpc.toml \
    && mkdir -p /certs/frp && chown -R sockless:sockless /certs/frp

COPY --from=frp /usr/local/bin/frpc /usr/local/bin/frpc
COPY --chmod=755 docker/start.sh /start.sh
COPY --chmod=755 docker/entrypoint.sh /entrypoint.sh
COPY --from=backend /app /app

WORKDIR /app
# The base image's default HTTP port would add a second listener; the app binds where
# AppAPI tells it (unix socket under HaRP, APP_HOST:APP_PORT otherwise).
ENV ASPNETCORE_HTTP_PORTS= \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Image default user stays root on purpose (see entrypoint.sh); the app itself runs as "sockless".
HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=10s --start-interval=1s \
    CMD curl -sf --unix-socket /tmp/exapp.sock http://localhost/heartbeat || curl -sf "http://127.0.0.1:${APP_PORT}/heartbeat" || exit 1

ENTRYPOINT ["/entrypoint.sh", "dotnet", "/app/SocklessMail.Api.dll"]
