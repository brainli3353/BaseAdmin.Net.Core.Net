# BaseAdminCore 本地依赖停止（Windows PowerShell）
# 用法: .\scripts\dev\down.ps1 [-Volumes]（加 -Volumes 清空数据卷）
[CmdletBinding()]
param([switch]$Volumes)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
Set-Location -LiteralPath $root

if ($Volumes) {
    Write-Host "==> 停止并清空数据卷" -ForegroundColor Yellow
    docker compose down -v
}
else {
    Write-Host "==> 停止依赖（保留数据卷）" -ForegroundColor Cyan
    docker compose down
}
