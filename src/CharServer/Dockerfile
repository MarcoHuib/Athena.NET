ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY src/CharServer/ ./src/CharServer/
RUN dotnet restore src/CharServer/CharServer.csproj
RUN dotnet publish src/CharServer/CharServer.csproj -c Release -o /app/out

FROM ${DOTNET_SDK_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app/out/ ./
RUN mkdir -p /app/conf
ENTRYPOINT ["dotnet", "CharServer.dll"]
