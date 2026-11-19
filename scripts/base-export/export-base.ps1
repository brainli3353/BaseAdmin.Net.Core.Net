param(
    [string]$Source = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Dest = '',
    [string]$Remote = '',
    [ValidateSet('FilterHistory', 'Snapshot')]
    [string]$Mode = 'FilterHistory',
    [switch]$Push,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Get-Allowlist {
    $path = Join-Path $PSScriptRoot 'allowlist.txt'
    if (-not (Test-Path -LiteralPath $path)) {
        Fail "Missing allowlist: $path"
    }
    $entries = Get-Content -LiteralPath $path -Encoding UTF8 |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
    if (-not $entries) {
        Fail 'Allowlist is empty.'
    }
    return $entries
}

function Test-Allowlisted([string]$RelativePath, [string[]]$Entries) {
    foreach ($entry in $Entries) {
        if ($RelativePath -eq $entry -or $RelativePath.StartsWith($entry + '/')) {
            return $true
        }
    }
    return $false
}

function Copy-Template([string]$Root, [string]$TemplateName, [string]$TargetRelativePath) {
    $source = Join-Path $PSScriptRoot ('templates\' + $TemplateName)
    if (-not (Test-Path -LiteralPath $source)) {
        Fail "Missing template: $source"
    }
    $target = Join-Path $Root $TargetRelativePath
    $parent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $source -Destination $target -Force
}

if (-not (Test-Path -LiteralPath $Source)) {
    Fail "Source repository not found: $Source"
}
if (-not (Test-Path -LiteralPath (Join-Path $Source '.git'))) {
    Fail "Source is not a git repository: $Source"
}

if (-not $Dest) {
    $Dest = Join-Path (Split-Path -Parent (Resolve-Path $Source)) 'BaseAdminCore-base'
}
$sourceFull = [System.IO.Path]::GetFullPath((Resolve-Path $Source).Path).TrimEnd('\', '/')
$destFull = [System.IO.Path]::GetFullPath($Dest).TrimEnd('\', '/')
if ($destFull -eq $sourceFull -or $destFull.StartsWith($sourceFull + [System.IO.Path]::DirectorySeparatorChar)) {
    Fail 'Destination must be outside the source repository.'
}

if (Test-Path -LiteralPath $destFull) {
    if (-not $Force) {
        Fail "Destination already exists: $destFull (use -Force to replace it)."
    }
    Remove-Item -LiteralPath $destFull -Recurse -Force
}
New-Item -ItemType Directory -Path $destFull -Force | Out-Null

$allowlist = Get-Allowlist
$head = (& git -C $Source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    Fail 'Cannot resolve source HEAD.'
}

Write-Host "[export] source : $sourceFull"
Write-Host "[export] dest   : $destFull"
Write-Host "[export] mode   : $Mode"
Write-Host "[export] head   : $head"

if ($Mode -eq 'FilterHistory') {
    & git filter-repo --version *> $null
    if ($LASTEXITCODE -ne 0) {
        Fail 'git-filter-repo is required for FilterHistory mode. Install it with: pip install git-filter-repo'
    }

    & git clone --quiet --no-hardlinks $sourceFull $destFull
    if ($LASTEXITCODE -ne 0) {
        Fail 'git clone failed.'
    }

    $tempList = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllLines($tempList, $allowlist, (New-Object System.Text.UTF8Encoding($false)))
        Push-Location $destFull
        try {
            & git filter-repo --force --paths-from-file $tempList
            if ($LASTEXITCODE -ne 0) {
                Fail 'git filter-repo failed.'
            }
        }
        finally {
            Pop-Location
        }
    }
    finally {
        Remove-Item -LiteralPath $tempList -Force -ErrorAction SilentlyContinue
    }
}
else {
    $tracked = & git -C $Source ls-tree -r --name-only HEAD
    if ($LASTEXITCODE -ne 0) {
        Fail 'git ls-tree failed.'
    }
    $copied = 0
    foreach ($relative in $tracked) {
        if (-not (Test-Allowlisted $relative $allowlist)) {
            continue
        }
        $from = Join-Path $sourceFull $relative
        $to = Join-Path $destFull $relative
        $parent = Split-Path -Parent $to
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $from -Destination $to -Force
        $copied++
    }
    Write-Host "[export] copied $copied tracked files"

    Push-Location $destFull
    try {
        & git init -b main --quiet
        if ($LASTEXITCODE -ne 0) {
            Fail 'git init failed.'
        }
    }
    finally {
        Pop-Location
    }
}

Copy-Template $destFull 'README.md' 'README.md'
Copy-Template $destFull 'docs-README.md' 'docs/README.md'

& (Join-Path $PSScriptRoot 'sanitize.ps1') -Root $destFull
if ($LASTEXITCODE -ne 0) {
    Fail 'sanitize.ps1 failed.'
}

Push-Location $destFull
try {
    $currentBranch = (& git branch --show-current).Trim()
    if ($currentBranch -and $currentBranch -ne 'main') {
        & git branch -M main
    }
    & git add -A
    if ($LASTEXITCODE -ne 0) {
        Fail 'git add failed.'
    }

    $name = (& git config user.name)
    if (-not $name) { $name = 'BaseAdminCore Export' }
    $email = (& git config user.email)
    if (-not $email) { $email = 'baseadmin-export@localhost' }

    if ($Mode -eq 'Snapshot') {
        & git -c "user.name=$name" -c "user.email=$email" commit --quiet -m "chore: export clean base snapshot from $head"
    }
    else {
        # filter-repo keeps history; the template/sanitize changes need their own commit.
        & git -c "user.name=$name" -c "user.email=$email" commit --quiet -m "chore: apply clean base templates and sanitize"
    }
    if ($LASTEXITCODE -ne 0) {
        Fail 'git commit failed (nothing to commit?).'
    }

    if ($Remote) {
        & git remote remove origin 2> $null
        & git remote add origin $Remote
        if ($LASTEXITCODE -ne 0) {
            Fail 'Failed to set origin remote.'
        }
    }

    if ($Push) {
        if (-not $Remote) {
            Fail '-Push requires -Remote.'
        }
        & git push -u origin main
        if ($LASTEXITCODE -ne 0) {
            Fail 'git push failed.'
        }
        & git push origin --tags
        if ($LASTEXITCODE -ne 0) {
            Fail 'git push --tags failed.'
        }
    }
}
finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot 'verify-base.ps1') -Root $destFull
if ($LASTEXITCODE -ne 0) {
    Fail 'verify-base.ps1 failed.'
}

Write-Host "[export] done: $destFull"
Write-Host "[export] next: inspect the result, then push or create the remote repository."
