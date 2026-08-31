# ViGEm Bus Driver インストール確認スクリプト
# 実行方法: PowerShellを管理者権限で開き、.\check-vigem.ps1 を実行

Write-Host "=== ViGEm Bus Driver 確認スクリプト ===" -ForegroundColor Cyan

# ViGEmBus ドライバの確認
$driver = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like "*ViGEm*" }
$service = Get-Service -Name "ViGEmBus" -ErrorAction SilentlyContinue

if ($driver -or $service) {
    Write-Host "[OK] ViGEm Bus Driver が検出されました。" -ForegroundColor Green
    if ($driver) {
        Write-Host "     デバイス名: $($driver.FriendlyName)" -ForegroundColor Green
        Write-Host "     ステータス:  $($driver.Status)" -ForegroundColor Green
    }
    if ($service) {
        Write-Host "     サービス状態: $($service.Status)" -ForegroundColor Green
    }
} else {
    Write-Host "[未インストール] ViGEm Bus Driver が見つかりませんでした。" -ForegroundColor Red
    Write-Host ""
    Write-Host "以下の手順でインストールしてください:" -ForegroundColor Yellow
    Write-Host "  1. 下記URLからインストーラをダウンロード" -ForegroundColor Yellow
    Write-Host "     https://github.com/nefarius/ViGEmBus/releases/latest" -ForegroundColor Yellow
    Write-Host "  2. ViGEmBus_Setup_x64.exe を管理者権限で実行" -ForegroundColor Yellow
    Write-Host "  3. インストール完了後、このスクリプトを再実行して確認" -ForegroundColor Yellow
    Write-Host ""

    $open = Read-Host "今すぐブラウザでダウンロードページを開きますか？ (y/n)"
    if ($open -eq "y") {
        Start-Process "https://github.com/nefarius/ViGEmBus/releases/latest"
    }
}

# .NET SDKの確認
Write-Host ""
Write-Host "=== .NET SDK 確認 ===" -ForegroundColor Cyan
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $version = dotnet --version
    Write-Host "[OK] .NET SDK: $version" -ForegroundColor Green
} else {
    Write-Host "[未インストール] .NET SDK が見つかりません。" -ForegroundColor Red
    Write-Host "     https://dotnet.microsoft.com/download からインストールしてください。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "確認完了。Enterキーで終了..." -ForegroundColor Gray
Read-Host
