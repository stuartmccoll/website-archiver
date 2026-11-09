FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app

COPY ./*.csproj /app/

RUN dotnet restore

FROM build AS publish

WORKDIR /app

COPY . .

RUN dotnet publish ./WebsiteArchiver.csproj -c Release -o ./app/bin/Release/net

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# Links the image published to ghcr.io back to this repository
LABEL org.opencontainers.image.source="https://github.com/stuartmccoll/website-archiver"
LABEL org.opencontainers.image.description="Crawls a website and archives its pages and stylesheets to Azure Blob Storage"

WORKDIR /app

COPY --from=publish /app/app/bin/Release/net ./app/bin/Release/net

ENTRYPOINT ["dotnet", "./app/bin/Release/net/WebsiteArchiver.dll"]
