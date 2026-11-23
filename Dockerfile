# Multi-arch image (linux/amd64, linux/arm64). Build with:
#   docker buildx build --platform linux/amd64,linux/arm64 -t flashforge-monitor .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/FlashforgeMonitor/FlashforgeMonitor.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY src/FlashforgeMonitor/ ./
RUN dotnet publish -a $TARGETARCH -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown $APP_UID /data
ENV Monitor__DataDirectory=/data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /data
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["./flashforge-monitor"]
