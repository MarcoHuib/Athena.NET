ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY src/LoginServer/ ./src/LoginServer/
RUN dotnet restore src/LoginServer/LoginServer.csproj
RUN dotnet publish src/LoginServer/LoginServer.csproj -c Release -o /app/out

FROM ${DOTNET_SDK_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app/out/ ./
RUN mkdir -p /app/conf
ENTRYPOINT ["dotnet", "LoginServer.dll"]
