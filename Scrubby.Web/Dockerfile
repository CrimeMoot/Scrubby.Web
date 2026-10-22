FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets ./
COPY Scrubby.Web/ Scrubby.Web/
RUN dotnet publish Scrubby.Web/Scrubby.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app ./

ENV IconSearch__ApiBaseUrl=/api/asset \
    AssetIndex__RemoteBaseUrl=https://raw.githubusercontent.com/CrimeMoot/Scrubby.Web/asset-data \
    DOTNET_NOLOGO=true

EXPOSE 8080
ENTRYPOINT ["dotnet", "Scrubby.Web.dll"]
