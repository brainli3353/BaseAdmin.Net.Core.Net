# BaseAdminCore 本地依赖一键启动（Windows PowerShell）
# 用法: .\scripts\dev\up.ps1 [-NoWait]
[CmdletBinding()]
param([switch]$NoWait)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
Set-Location -LiteralPath $root

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "未找到 docker，请先安装 Docker Desktop 并启动。"
}

Write-Host "==> 启动 PostgreSQL + Redis" -ForegroundColor Cyan
docker compose up -d

if ($NoWait) { return }

Write-Host "==> 等待依赖就绪" -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    $ps = docker compose ps --format json 2>$null
    if ($ps) {
        $unhealthy = $ps | Where-Object { $_ -match '"Health"\s*:\s*"unhealthy"' }
        $running   = $ps | Where-Object { $_ -match '"State"\s*:\s*"running"' }
        if ($running -and -not $unhealthy) {
            # 再确认健康字段存在
            $healthy = $ps | Where-Object { $_ -match '"Health"\s*:\s*"healthy"' }
            if ($healthy) { break }
        }
    }
    Start-Sleep -Seconds 3
}

docker compose ps
Write-Host ""
Write-Host "PostgreSQL: localhost:$((Get-Content .env -ErrorAction SilentlyContinue | Select-String '^POSTGRES_PORT=' | ForEach-Object { ($_ -split '=')[1] }) ?? 5432)" -ForegroundColor Green
Write-Host "Redis:      localhost:6379" -ForegroundColor Green
Write-Host "下一步: dotnet run --project src/Web.Entry -c Debug" -ForegroundColor Yellow
