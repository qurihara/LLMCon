# LLMCon インストールスクリプト
#
# このスクリプトは次の3つを行います。
#   1. ViGEmBus ドライバが入っていなければ winget で導入する（ドライバ導入時に管理者の確認が出ます）
#   2. 単一ファイルの実行ファイルをインストール先へコピーする
#   3. スタートメニューにショートカットを作る
#
# 使い方:
#   先に publish フォルダを作っておきます（installer/README.md を参照）。
#   その後このスクリプトを実行します。実行ファイルは publish フォルダ、または
#   このスクリプトと同じ場所にある CntlLevelConnection.exe / LLMCon.exe を探します。

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'LLMCon'

# コピー元の実行ファイルを探す
$candidates = @(
    (Join-Path $PSScriptRoot 'LLMCon.exe'),
    (Join-Path $PSScriptRoot 'CntlLevelConnection.exe'),
    (Join-Path $PSScriptRoot '..\publish\CntlLevelConnection.exe')
)
$src = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $src) {
    Write-Host "実行ファイルが見つかりませんでした。先に publish フォルダを作成してください（installer/README.md を参照）。" -ForegroundColor Red
    exit 1
}

# 1. ViGEmBus ドライバ
$svc = Get-Service -Name 'ViGEmBus' -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "ViGEmBus ドライバが見つかりません。winget で導入します（管理者の確認が出ます）。" -ForegroundColor Yellow
    winget install --id ViGEm.ViGEmBus --exact --silent --accept-package-agreements --accept-source-agreements
} else {
    Write-Host "ViGEmBus ドライバは導入済みです。" -ForegroundColor Green
}

# 2. 実行ファイルをコピー
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$destExe = Join-Path $installDir 'LLMCon.exe'
Copy-Item -Path $src -Destination $destExe -Force
Write-Host "インストール先: $destExe" -ForegroundColor Green

# 3. スタートメニューのショートカット
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $programs 'LLMCon.lnk'))
$shortcut.TargetPath = $destExe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'LLMCon - MCP制御できる仮想ゲームコントローラ'
$shortcut.Save()

Write-Host ""
Write-Host "インストールが完了しました。スタートメニューの LLMCon から起動できます。" -ForegroundColor Green
Write-Host "起動すると、MCP サーバが http://127.0.0.1:8777/ で待ち受けます。" -ForegroundColor Green
Write-Host "2台目を動かすときは、コマンドラインで LLMCon.exe --port 8778 --name 2P のように指定してください。" -ForegroundColor Green
