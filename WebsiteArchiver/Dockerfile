FROM mcr.microsoft.com/dotnet/sdk

COPY bin/Release/net5.0/publish /app

WORKDIR /app

ENTRYPOINT ["dotnet", "WebsiteArchiver.dll"]
