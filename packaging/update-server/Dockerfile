# syntax=docker/dockerfile:1

# ---------- 构建 ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只拷贝工程文件，利用层缓存：改业务代码不会重新还原依赖
COPY Directory.Build.props global.json ./
COPY src/MailClient.Update.Protocol/MailClient.Update.Protocol.csproj src/MailClient.Update.Protocol/
COPY src/MailClient.Update.Protocol/packages.lock.json src/MailClient.Update.Protocol/
COPY src/MailClient.UpdateServer/MailClient.UpdateServer.csproj src/MailClient.UpdateServer/
COPY src/MailClient.UpdateServer/packages.lock.json src/MailClient.UpdateServer/
RUN dotnet restore src/MailClient.UpdateServer/MailClient.UpdateServer.csproj

COPY src/MailClient.Update.Protocol/ src/MailClient.Update.Protocol/
COPY src/MailClient.UpdateServer/ src/MailClient.UpdateServer/
RUN dotnet publish src/MailClient.UpdateServer/MailClient.UpdateServer.csproj \
        -c Release -o /app --no-restore /p:UseAppHost=false

# ---------- 运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# 健康检查需要 curl；Debian 镜像默认不带
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    UPDATESERVER_DATA=/data \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

RUN mkdir -p /data && chown -R app:app /data
COPY --from=build /app ./

USER app
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["curl", "-fsS", "http://127.0.0.1:8080/healthz"]

ENTRYPOINT ["dotnet", "MailClient.UpdateServer.dll"]
