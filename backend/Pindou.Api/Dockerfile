# 文件：Dockerfile
# 用途：构建并运行 Linux 环境下的拼豆 C# API 镜像。
# 核心职责：使用多阶段构建缩小运行镜像，并固定服务监听端口。
# 版权：@董志伟-联系方式-makabak1204
# 最后修改：2026-08-21

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Pindou.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-noto-cjk \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
# 中-06：为 app 用户创建可写的令牌数据目录（持久化卷挂载点），容器重建后管理令牌不丢失。
RUN mkdir -p /app/data && chown -R app:app /app
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Pindou.Api.dll"]
