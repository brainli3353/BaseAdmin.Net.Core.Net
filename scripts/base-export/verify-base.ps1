param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$rootFull = [System.IO.Path]::GetFullPath((Resolve-Path $Root).Path).TrimEnd('\', '/')
$failures = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath (Join-Path $rootFull '.git'))) {
    Write-Error "Not a git repository: $rootFull"
    exit 1
}

$required = @(
    'README.md',
    'docs/README.md',
    'docs/框架索引.md',
    'docs/框架使用指南.md',
    'docs/框架开发参考手册.md',
    'docs/baseadmin设计精华参考.md',
    'src/AdminCore.slnx',
    'src/Core',
    'src/Web.Core',
    'src/Web.Entry',
    'src/Business',
    'web/package.json',
    # G37（2026-10-25 批准）：t674 脚手架与项目地图模板须随发布带出（放行预检＝forbidden 零命中、
    # sanitize 仅自动改写地图模板默认口令 1 处 Admin@123→ChangeMe@123）。
    'templates/dotnet-repo/README.md',
    'templates/dotnet-repo/scripts/doctor.sh',
    'docs/项目地图_模板.md'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $rootFull $relative))) {
        $failures.Add("missing required path: $relative")
    }
}

$forbiddenPaths = @(
    'CLAUDE.md',
    'plans',
    'docs/archive',
    'docs/研究',
    'docs/业务',
    'docs/框架差距清单.md',
    'docs/框架功能清单.md',
    'docs/立项.md',
    'docs/环境管理.md',
    'docs/自主推进_BaseAdminCore.md',
    'docs/sqlsugar升级评估-G7G9.md'
)
foreach ($relative in $forbiddenPaths) {
    if (Test-Path -LiteralPath (Join-Path $rootFull $relative)) {
        $failures.Add("forbidden path present: $relative")
    }
}

$forbiddenFile = Join-Path $PSScriptRoot 'forbidden.txt'
if (-not (Test-Path -LiteralPath $forbiddenFile)) {
    $failures.Add("missing forbidden pattern file: $forbiddenFile")
}
else {
    Push-Location $rootFull
    try {
        $matches = & git grep -n -I -i -E -f $forbiddenFile -- .
        if ($LASTEXITCODE -gt 1) {
            $failures.Add('git grep failed while scanning forbidden content.')
        }
        elseif ($matches) {
            $failures.Add('forbidden content found:')
            foreach ($line in $matches) {
                $failures.Add("  $line")
            }
        }

        $historyPaths = & git log --all --pretty=format: --name-only
        if ($LASTEXITCODE -ne 0) {
            $failures.Add('git log failed while checking history.')
        }
        foreach ($line in $historyPaths) {
            if (-not $line) { continue }
            foreach ($relative in $forbiddenPaths) {
                if ($line -eq $relative -or $line.StartsWith($relative + '/')) {
                    $failures.Add("forbidden path in history: $line")
                }
            }
        }
    }
    finally {
        Pop-Location
    }
}

if ($failures.Count -gt 0) {
    Write-Host '[verify] FAILED'
    foreach ($failure in ($failures | Select-Object -Unique)) {
        Write-Host "  $failure"
    }
    exit 1
}

Write-Host '[verify] OK: required paths present, forbidden paths/content absent.'
