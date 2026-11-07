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
# 証明書の置き場(用途別ディレクトリの pfx と、署名用の公開鍵 public/*.cer)。永続化すること。
# pfx は秘密鍵を含むので 0700(アプリが cert/ の下に作るディレクトリも 0700)。
# Crew と TTC は、同じホストで cert/public/ だけを読み取り専用でマウントして使う(Docs/certificate-rotation.md)。
# public/ はアプリが 0755 で作るので、cert/ が 0700 でも、public/ をマウントした側からは読める。
RUN mkdir -p /app/cert && chown $APP_UID:$APP_UID /app/cert && chmod 0700 /app/cert
VOLUME /app/cert
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Tatehama.Shukkindai.Server.dll"]
