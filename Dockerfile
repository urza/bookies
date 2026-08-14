FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore before copying the rest, so source edits don't invalidate the package layer.
COPY Bookies.csproj .
RUN dotnet restore

COPY . .
# Name the project explicitly: the solution file is copied in above, and publishing a solution to
# a single output directory is unsupported (NETSDK1194).
RUN dotnet publish Bookies.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .

# Runs unprivileged. /data is created here so the image works even without a mounted volume.
RUN addgroup -g 1000 bookies \
    && adduser -D -u 1000 -G bookies bookies \
    && mkdir -p /data \
    && chown -R bookies:bookies /data
USER bookies

ENV ASPNETCORE_URLS=http://+:8080 \
    BOOKIES__DATADIR=/data

EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q -O- http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Bookies.dll"]
