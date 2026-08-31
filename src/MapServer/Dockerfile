ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY src/MapServer/ ./src/MapServer/
RUN dotnet restore src/MapServer/MapServer.csproj
RUN dotnet publish src/MapServer/MapServer.csproj -c Release -o /app/out

FROM ${DOTNET_SDK_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app/out/ ./
# Pinned rAthena's own map_cache.dat (legacy/rathena is a git submodule - see .gitmodules; the
# exact pinned commit is e985006171d2eb320ee512a653f4c83aea3d81b6, tracked independently of this
# Dockerfile) is the normal Athena world-geometry source (ai/world-data.md "Map geometry: direct
# pinned rAthena map_cache.dat import"). It is baked into the image at the SAME relative path
# (legacy/rathena/db/map_cache.dat) local `dotnet run` from the repo root already resolves
# map_cache_path against, so one repository-relative config value (conf/map_athena.conf's
# `map_cache_path: legacy/rathena/db/map_cache.dat`) works unchanged for local dev, Docker, and
# CI alike - no separate absolute-path config branch, no bind-mount, and no copy/conversion of
# individual map cells into generated code. Only the one committed data file is copied - never the
# full submodule checkout (scripts/tools/source), keeping the image small and avoiding any
# temptation to run rAthena code inside this container.
RUN mkdir -p /app/conf
RUN chown -R 1000:1000 /app
USER 1000
ENTRYPOINT ["dotnet", "MapServer.dll"]
