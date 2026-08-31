# ビルド & 実行スクリプト
# 実行方法: PowerShellで .\build-and-run.ps1

$projectDir = Join-Path $PSScriptRoot "CntlLevelConnection"
$csproj     = Join-Path $projectDir "CntlLevelConnection.csproj"

Write-Host "=== Controller Level Connection ビルド ===" -ForegroundColor Cyan

# dotnet が使えるか確認
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[エラー] dotnet コマンドが見つかりません。" -ForegroundColor Red
    Write-Host "  https://dotnet.microsoft.com/download からインストールしてください。"
    exit 1
}

Write-Host "NuGet パッケージを復元中..." -ForegroundColor Gray
dotnet restore $csproj
if ($LASTEXITCODE -ne 0) { Write-Host "復元失敗" -ForegroundColor Red; exit 1 }

Write-Host "ビルド中..." -ForegroundColor Gray
dotnet build $csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { Write-Host "ビルド失敗" -ForegroundColor Red; exit 1 }

Write-Host "[OK] ビルド成功！アプリを起動します..." -ForegroundColor Green
dotnet run --project $csproj -c Release
