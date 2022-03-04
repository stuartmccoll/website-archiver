FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build

WORKDIR /app

COPY ./*.csproj /app/

RUN dotnet restore

FROM build as publish

WORKDIR /app

COPY . .

RUN dotnet publish ./WebsiteArchiver.csproj -c Release -o ./app/bin/Release/net

FROM mcr.microsoft.com/dotnet/aspnet:5.0 as runtime

WORKDIR /app

COPY --from=publish /app/app/bin/Release/net ./app/bin/Release/net

ENTRYPOINT ["dotnet", "./app/bin/Release/net/WebsiteArchiver.dll"]
