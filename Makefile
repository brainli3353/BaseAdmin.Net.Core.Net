# BaseAdminCore 常用命令（需要 make；Windows 可用 git bash / WSL，或直接用 scripts/dev/*.ps1）
# 变量可用环境覆盖，如：make up POSTGRES_PORT=5433

POSTGRES_PORT ?= 5432
REDIS_PORT    ?= 6379
CONFIG        ?= Debug

.PHONY: help up down logs ps build test test-zero web-install web-dev web-build web-typecheck clean

help: ## 显示帮助
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

up: ## 启动 PostgreSQL + Redis（docker compose）
	docker compose up -d

down: ## 停止依赖（保留数据卷）
	docker compose down

logs: ## 查看依赖日志
	docker compose logs -f

ps: ## 查看依赖状态
	docker compose ps

build: ## 严格门构建（0W/0E）
	dotnet build src/Web.Entry/AdminCore.Web.Entry.csproj -warnaserror

test: ## 全量回归（真实 PG，约 10–15 分钟）
	dotnet test src/Test/AdminCore.Test.csproj --logger "console;verbosity=detailed"

test-zero: ## 零库档快测（无需 PG/Redis）
	dotnet test src/Test/AdminCore.Test.csproj --filter "FullyQualifiedName~BatchSGErrCodeCoverageEnumColumn|FullyQualifiedName~BatchAQErrorCode|FullyQualifiedName~BatchZEnum|FullyQualifiedName~BatchOValidation|FullyQualifiedName~BatchAEAdo|FullyQualifiedName~BatchALLocalization|FullyQualifiedName~BatchT48CacheInvalidationGuard|FullyQualifiedName~BatchBackflowFileLogger|FullyQualifiedName~BatchBackflowPagingGuard"

web-install: ## 安装前端依赖（pnpm）
	cd web && pnpm install

web-dev: ## 前端开发服务器（代理到 5090）
	cd web && pnpm dev

web-build: ## 前端构建
	cd web && pnpm build

web-typecheck: ## 前端类型检查
	cd web && pnpm typecheck

clean: ## 清理构建产物
	dotnet clean src/AdminCore.slnx --nologo -v q
