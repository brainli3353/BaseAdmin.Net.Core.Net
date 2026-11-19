param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$rootFull = [System.IO.Path]::GetFullPath((Resolve-Path $Root).Path).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath (Join-Path $rootFull '.git'))) {
    Write-Error "Not a git repository: $rootFull"
    exit 1
}

$textExtensions = @(
    '.md', '.cs', '.json', '.ts', '.vue', '.csproj', '.props', '.slnx', '.sln',
    '.html', '.css', '.scss', '.yml', '.yaml', '.txt', '.ps1', '.xml', '.config'
)
$skipDirs = @('.git', 'node_modules', 'bin', 'obj', 'dist', '.vs', '.idea', 'logs')

$replacements = @(
    [pscustomobject]@{ Pattern = 'Admin@123'; Value = 'ChangeMe@123' },
    [pscustomobject]@{ Pattern = 'hulubao'; Value = 'deploy-host' },
    [pscustomobject]@{ Pattern = '192\.168\.1\.202'; Value = 'test-host' },
    [pscustomobject]@{ Pattern = '172\.18\.[0-9.]+'; Value = 'db-host' },
    [pscustomobject]@{ Pattern = '（原名 huluyeye-admin）'; Value = '' },
    [pscustomobject]@{ Pattern = 'huluyeye_admin_test'; Value = 'baseadmin_test' },
    [pscustomobject]@{ Pattern = 'huluyeye_admin'; Value = 'baseadmin' },
    [pscustomobject]@{ Pattern = 'huluyeye-admin'; Value = 'BaseAdminCore' },
    [pscustomobject]@{ Pattern = 'huluyeye'; Value = 'BaseAdminCore' },
    [pscustomobject]@{ Pattern = '\.\./MainWorkspace/docs/全局公约\.md'; Value = '项目编码约定' },
    [pscustomobject]@{ Pattern = 'MainWorkspace/docs/全局公约\.md'; Value = '项目编码约定' },
    [pscustomobject]@{ Pattern = 'docs/全局公约\.md'; Value = '项目编码约定' },
    [pscustomobject]@{ Pattern = 'MainWorkspace'; Value = '工作区' },
    [pscustomobject]@{ Pattern = '葫芦爷爷'; Value = '项目维护者' },
    [pscustomobject]@{ Pattern = 'docs/研究/[A-Za-z0-9_./-]+\.md'; Value = '内部移植调研记录' },
    [pscustomobject]@{ Pattern = 'docs/archive/[A-Za-z0-9_./-]+'; Value = '内部历史归档' },
    [pscustomobject]@{ Pattern = 'docs/业务/规划/[^\s)]+'; Value = '内部规划台账' },
    [pscustomobject]@{ Pattern = 'docs/上送_[^\s)]+\.md'; Value = '内部流程记录' },
    [pscustomobject]@{ Pattern = 'docs/sqlsugar升级评估-G7G9\.md'; Value = '内部升级评估' },
    [pscustomobject]@{ Pattern = 'docs/自主推进_BaseAdminCore\.md'; Value = '内部工作记录' },
    [pscustomobject]@{ Pattern = 'docs/框架功能清单\.md'; Value = '框架功能概览' },
    [pscustomobject]@{ Pattern = 'docs/框架差距清单\.md'; Value = '内部差距清单' },
    [pscustomobject]@{ Pattern = 'docs/立项\.md'; Value = '项目立项记录' },
    [pscustomobject]@{ Pattern = 'docs/环境管理\.md'; Value = '环境部署说明' },
    [pscustomobject]@{ Pattern = '框架功能清单\.md'; Value = '框架索引.md' },
    [pscustomobject]@{ Pattern = '框架差距清单\.md'; Value = '内部维护台账（不在本仓）' },
    [pscustomobject]@{ Pattern = 'plans/项目计划书\.md'; Value = '内部项目计划' },
    [pscustomobject]@{ Pattern = 'plans/[^\s)]+'; Value = '内部计划资料' },
    [pscustomobject]@{ Pattern = 'docs/研究'; Value = '内部移植调研记录' },
    [pscustomobject]@{ Pattern = 'docs/archive'; Value = '内部历史归档' },
    [pscustomobject]@{ Pattern = 'docs/业务'; Value = '内部规划台账' },
    [pscustomobject]@{ Pattern = 'docs/框架差距清单'; Value = '内部差距清单' },
    [pscustomobject]@{ Pattern = 'CLAUDE\.md 硬性约定'; Value = '项目开发约定' },
    [pscustomobject]@{ Pattern = 'BaseAdminCore框架开发参考手册'; Value = 'BaseAdminCore 框架开发参考手册' },
    [pscustomobject]@{ Pattern = 'CLAUDE\.md'; Value = '项目开发约定' }
)

$changed = 0
$scanned = 0
$files = Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force
foreach ($file in $files) {
    $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\', '/')
    $parts = $relative -split '[\\/]'
    $skip = $false
    foreach ($part in $parts) {
        if ($skipDirs -contains $part) {
            $skip = $true
            break
        }
    }
    if ($skip) {
        continue
    }
    if ($textExtensions -notcontains $file.Extension.ToLowerInvariant()) {
        continue
    }

    $scanned++
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $updated = $text
    foreach ($replacement in $replacements) {
        $updated = [regex]::Replace($updated, $replacement.Pattern, $replacement.Value, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    if ($updated -ne $text) {
        $encoding = New-Object System.Text.UTF8Encoding($hasBom)
        [System.IO.File]::WriteAllText($file.FullName, $updated, $encoding)
        $changed++
        Write-Host "[sanitize] $relative"
    }
}

Write-Host "[sanitize] scanned $scanned files, changed $changed"
