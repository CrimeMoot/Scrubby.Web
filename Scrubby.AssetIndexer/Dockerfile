# Headless asset index builder. Build from the repository root:
#   docker build -f Scrubby.AssetIndexer/Dockerfile -t scrubby-asset-indexer .
#   docker run --rm -v "$PWD/out:/out" scrubby-asset-indexer
#
# Output (compact index) is written to /out: repos.json + index/*.json.gz

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets ./
COPY Scrubby.Web/ Scrubby.Web/
COPY Scrubby.AssetIndexer/ Scrubby.AssetIndexer/
RUN dotnet publish Scrubby.AssetIndexer/Scrubby.AssetIndexer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app ./
COPY Scrubby.Web/AssetIndex/repos.json /app/catalog/repos.json

ENV AssetIndex__ReposPath=/app/catalog/repos.json \
    AssetIndex__DataDirectory=/out \
    AssetIndex__RenderAssets=false \
    DOTNET_NOLOGO=true

VOLUME ["/out"]
ENTRYPOINT ["dotnet", "Scrubby.AssetIndexer.dll"]
