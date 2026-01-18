ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY src/MapServer/ ./src/MapServer/
RUN dotnet restore src/MapServer/MapServer.csproj
RUN dotnet publish src/MapServer/MapServer.csproj -c Release -o /app/out

FROM ${DOTNET_SDK_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app/out/ ./
RUN mkdir -p /app/conf
ENTRYPOINT ["dotnet", "MapServer.dll"]
