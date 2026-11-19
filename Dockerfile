# BaseAdminCore 全栈镜像（可选）：前端构建 + 后端发布 + 运行时
# 依赖（PG/Redis）由 docker-compose.yml 提供；本镜像经 docker-compose.app.yml 起用。

# ---- 1. 前端构建（Vue3 + Vite） ----
FROM node:24-alpine AS webbuild
WORKDIR /web
RUN corepack enable && corepack prepare pnpm@10.33.0 --activate
COPY web/package.json web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm build

# ---- 2. 后端发布（.NET 10） ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NuGet.config ./
COPY thirdparty ./thirdparty
COPY src ./src
RUN dotnet publish src/Web.Entry/AdminCore.Web.Entry.csproj -c Release -o /app

# ---- 3. 运行时 ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=webbuild /web/dist ./web/dist
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5090
ENTRYPOINT ["dotnet", "AdminCore.Web.Entry.dll"]
