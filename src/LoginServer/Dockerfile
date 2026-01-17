ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-preview

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY athenadotnet/src/LoginServer/ ./athenadotnet/src/LoginServer/
RUN dotnet restore athenadotnet/src/LoginServer/LoginServer.csproj
RUN dotnet publish athenadotnet/src/LoginServer/LoginServer.csproj -c Release -o /app/out

FROM ${DOTNET_SDK_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app/out/ ./
COPY conf/ ./conf/
ENTRYPOINT ["dotnet", "LoginServer.dll"]
