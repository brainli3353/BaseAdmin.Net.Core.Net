# 获取压测 JWT (两阶段 OTP 登录): 打印 access token 到 stdout
# 用法: .\scripts\get-token.ps1 [-BaseUrl http://127.0.0.1:5090]
param([string]$BaseUrl = "http://127.0.0.1:5090")

$body = @{ account = 'admin'; password = 'Admin@123' } | ConvertTo-Json
$login = Invoke-RestMethod -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 15
$data = $login.data

if ($null -ne $data.token) {
    Write-Output $data.token   # OTP 开关已关,直登返回 token
    exit 0
}

if ($null -ne $data.challengeId) {
    Write-Host "请输入手机验证器里当前的 6 位动态码: " -NoNewline
    $code = Read-Host
    $otp = @{ challengeId = $data.challengeId; code = $code } | ConvertTo-Json
    $r = Invoke-RestMethod -Uri "$BaseUrl/api/auth/login/otp" -Method Post -ContentType 'application/json' -Body $otp -TimeoutSec 15
    Write-Output $r.data
    exit 0
}

Write-Error "登录失败: $($data | ConvertTo-Json -Depth 5)"
exit 1