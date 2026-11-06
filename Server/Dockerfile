# ビルドコンテキストはリポジトリのルート: docker build -f Server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY Server/Tatehama.Shukkindai.Server/Tatehama.Shukkindai.Server.csproj Server/Tatehama.Shukkindai.Server/
RUN dotnet restore Server/Tatehama.Shukkindai.Server/Tatehama.Shukkindai.Server.csproj
COPY Server/Tatehama.Shukkindai.Server/ Server/Tatehama.Shukkindai.Server/
COPY Common/ Common/
RUN dotnet publish Server/Tatehama.Shukkindai.Server -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# 証明書(署名・暗号化の pfx と .cer)の置き場。永続化すること
RUN mkdir -p /app/cert && chown $APP_UID:$APP_UID /app/cert
VOLUME /app/cert
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Tatehama.Shukkindai.Server.dll"]
