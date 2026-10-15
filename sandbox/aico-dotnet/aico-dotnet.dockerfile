# syntax=docker/dockerfile:1
# Staged under /out and copied onto scratch, so the overlay lands on the Claude workload without touching /home.
FROM debian:trixie-slim AS build
ARG TARGETARCH
ARG DOTNET_CHANNEL
ARG ACTIONLINT_VERSION
ARG SONARSCANNER_VERSION
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl bash \
 && rm -rf /var/lib/apt/lists/*

# .NET SDK into /usr/share/dotnet (the apphost's default location on Linux), dotnet on PATH via /usr/local/bin.
RUN set -eux; \
    case "$TARGETARCH" in amd64) arch=x64 ;; arm64) arch=arm64 ;; *) echo "unsupported $TARGETARCH" >&2; exit 1 ;; esac; \
    curl -fsSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o /tmp/dotnet-install.sh; \
    bash /tmp/dotnet-install.sh --channel "${DOTNET_CHANNEL}" --architecture "$arch" --install-dir /out/usr/share/dotnet --no-path; \
    mkdir -p /out/usr/local/bin /out/etc/dotnet; \
    ln -s /usr/share/dotnet/dotnet /out/usr/local/bin/dotnet; \
    echo /usr/share/dotnet > /out/etc/dotnet/install_location; \
    /out/usr/share/dotnet/dotnet --list-sdks | grep -q "^${DOTNET_CHANNEL}\."

# actionlint: a static release binary named by GOARCH, which is what TARGETARCH carries.
RUN set -eux; \
    curl -fsSL "https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_linux_${TARGETARCH}.tar.gz" \
      | tar -xz -C /out/usr/local/bin actionlint; \
    /out/usr/local/bin/actionlint -version | grep -q "${ACTIONLINT_VERSION}"

# dotnet-sonarscanner as a tool-path tool (no global tool manifest under /home); `dotnet sonarscanner` finds it on PATH.
RUN set -eux; \
    DOTNET_ROOT=/out/usr/share/dotnet /out/usr/share/dotnet/dotnet tool install dotnet-sonarscanner \
      --version "${SONARSCANNER_VERSION}" --tool-path /out/opt/dotnet-tools; \
    ln -s /opt/dotnet-tools/dotnet-sonarscanner /out/usr/local/bin/dotnet-sonarscanner; \
    ls /out/opt/dotnet-tools/.store/dotnet-sonarscanner | grep -qx "${SONARSCANNER_VERSION}"

RUN chown -R 0:0 /out

FROM scratch
COPY --from=build /out /
