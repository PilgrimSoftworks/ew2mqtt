# syntax=docker/dockerfile:1.7

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETOS
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props Pilgrim.EasyWorship.slnx ./
COPY src ./src
COPY tests ./tests

RUN case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "unsupported arch $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/Pilgrim.EasyWorship.Mqtt -c Release -r $RID \
        -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true \
        -p:DebugType=embedded -p:Version=$VERSION \
        -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.source=https://github.com/PilgrimSoftworks/ew2mqtt
LABEL org.opencontainers.image.licenses=MIT
LABEL org.opencontainers.image.title=ew2mqtt
LABEL org.opencontainers.image.description="EasyWorship-to-MQTT bridge"

WORKDIR /app
COPY --from=build /app/ew2mqtt /app/ew2mqtt
COPY --from=build /app/appsettings.json /app/appsettings.json

USER 1000:1000
ENTRYPOINT ["/app/ew2mqtt"]
