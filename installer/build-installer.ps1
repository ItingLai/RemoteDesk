# =============================================================================
# RemoteDesk WiX Toolset + Burn Bootstrapper 自動化打包發布指令碼
# 部署策略：Framework-dependent Deployment (.NET 9 Desktop Runtime x64 線上下載)
# =============================================================================
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Architecture = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path "$PSScriptRoot/.."
$publishDir = "$repoRoot/publish"
$distDir = "$repoRoot/dist"
$cacheDir = "$PSScriptRoot/cache"
$runtimeFile = "$cacheDir/windowsdesktop-runtime-9.0.22-win-x64.exe"
$runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.22/windowsdesktop-runtime-9.0.22-win-x64.exe"
$expectedSha256 = "79777A9E4D95F9F10126C2BC2BAC673DF987A29B64CE107B3FD1704F63633865"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  RemoteDesk WiX Burn + MSI 打包建置流程開始" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. 確保快取與輸出資料夾存在
if (-not (Test-Path $cacheDir)) { New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null }
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }

# 2. 檢查或下載 Microsoft 官方 .NET 9 Desktop Runtime x64 快取
if (-not (Test-Path $runtimeFile)) {
    Write-Host "[1/5] 下載微軟官方 .NET 9 Desktop Runtime x64 安裝程式..." -ForegroundColor Yellow
    curl.exe -L -o $runtimeFile $runtimeUrl
}

# 3. 驗證 Runtime 檔案完整性與 SHA256
Write-Host "[2/5] 驗證 Runtime 安裝檔 SHA256 雜湊與數位簽章..." -ForegroundColor Yellow
$actualSha256 = (Get-FileHash -Path $runtimeFile -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "Runtime SHA256 驗證失敗！預期: $expectedSha256，實際: $actualSha256"
}
$sigStatus = (Get-AuthenticodeSignature -FilePath $runtimeFile).Status
Write-Host "  -> SHA256 驗證通過: $actualSha256" -ForegroundColor Green
Write-Host "  -> 微軟官方數位簽章狀態: $sigStatus" -ForegroundColor Green

# 4. 發布 Framework-dependent 主程式
Write-Host "[3/5] 發布 Framework-dependent 主程式 (net9.0-windows, x64)..." -ForegroundColor Yellow
Stop-Process -Name "RemoteDesk" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
dotnet publish "$repoRoot/src/RemoteDesk/RemoteDesk.csproj" -c $Configuration -r $Architecture --self-contained false -o $publishDir
Write-Host "  -> 主程式發布完成: $publishDir" -ForegroundColor Green

# 5. 使用 WiX Toolset 編譯 MSI 安裝包
Write-Host "[4/5] 使用 WiX Toolset 建置 RemoteDesk.msi..." -ForegroundColor Yellow
$msiOut = "$distDir/RemoteDesk.msi"
wix build "$PSScriptRoot/Package.wxs" -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -o $msiOut
Write-Host "  -> MSI 建置完成: $msiOut ($( (Get-Item $msiOut).Length ) bytes)" -ForegroundColor Green

# 6. 使用 WiX Burn Bootstrapper 建置 RemoteDeskSetup.exe
Write-Host "[5/5] 使用 WiX Burn 建置 RemoteDeskSetup.exe..." -ForegroundColor Yellow
$setupOut = "$distDir/RemoteDeskSetup.exe"
wix build "$PSScriptRoot/Bundle.wxs" -arch x64 -ext WixToolset.BootstrapperApplications.wixext -ext WixToolset.Netfx.wixext -o $setupOut
Write-Host "  -> Bootstrapper 建置完成: $setupOut ($( (Get-Item $setupOut).Length ) bytes)" -ForegroundColor Green

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  打包完成！產出檔案清單：" -ForegroundColor Green
Get-Item "$distDir/RemoteDeskSetup.exe", "$distDir/RemoteDesk.msi" | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "==========================================================" -ForegroundColor Cyan
