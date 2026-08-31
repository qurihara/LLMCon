# HidHide が物理パッドを隠せているかを、隠す前と後で測る道具。
#
# 大事なこと: --dev-hide に渡すのは「デバイスインスタンスパス」である。
#   効く   HID\VID_0F0D&PID_0092\6&617E745&0&0000
#   効かない \\?\hid#vid_0f0d&pid_0092#6&617e745&0&0000#{4d1e55b2-...}
# 間違った形式を渡してもエラーは出ず、--dev-list には並ぶ。黙って効かないだけである。
#
# 使い方:
#   .\tools\hidhide-check.ps1              いま繋がっているパッドで試す
#   .\tools\hidhide-check.ps1 -Restore     隠蔽を止めて元に戻すだけ
#
# 管理者権限は要らない（HidHide の導入だけは要る）。
param(
  [switch]$Restore
)
$ErrorActionPreference = "Continue"

$cli = "C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"
if (-not (Test-Path $cli)) { Write-Output "HidHide が入っていません: $cli"; exit 1 }

$probe = Join-Path $PSScriptRoot "wgiprobe\bin\Release\net8.0-windows10.0.19041.0\wgiprobe.exe"
if (-not (Test-Path $probe)) { Write-Output "先に dotnet build tools/wgiprobe -c Release を実行してください"; exit 1 }

function Count-Pads {
  $out = & $probe --seconds=1 2>&1 | Out-String
  if ($out -match 'RawGameControllers\s*:\s*(\d+)') { return [int]$Matches[1] }
  return -1
}

if ($Restore) {
  & $cli --cloak-off | Out-Null
  Write-Output "隠蔽を止めました。いま見えるパッド: $(Count-Pads) 台"
  & $cli --cloak-state
  exit 0
}

# いま繋がっているゲームパッドのインスタンスパスを、レジストリから拾う。
# HidHideCLI --dev-gaming は日本語環境で JSON が壊れるので使わない。
$guid = "{4d1e55b2-f16f-11cf-88cb-001111000030}"
$root = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$guid"
$present = Get-PnpDevice -Class HIDClass -Status OK -ErrorAction SilentlyContinue |
  Where-Object { $_.FriendlyName -match 'game controller|ゲーム' } |
  Select-Object -ExpandProperty InstanceId

if (-not $present) { Write-Output "ゲームパッドが繋がっていません"; exit 1 }

Write-Output "=== 対象 ==="
$present | ForEach-Object { Write-Output "  $_" }
Write-Output ""

& $cli --cloak-off | Out-Null
$before = Count-Pads
Write-Output "隠す前 : $before 台"

foreach ($p in $present) { & $cli --dev-hide "$p" | Out-Null }
& $cli --cloak-on | Out-Null
Start-Sleep -Milliseconds 500
$after = Count-Pads
Write-Output "隠した後: $after 台"
Write-Output ""

Write-Output "=== 結果 ==="
if ($before -gt 0 -and $after -eq 0) {
  Write-Output "OK  隠れました。HidHide はこの機体に効きます。"
} elseif ($before -eq $after) {
  Write-Output "NG  台数が変わりません。形式か、フィルタの組み込みを疑ってください。"
  Write-Output "    デバイスの階層に \Driver\HidHide があるか確認:"
  Write-Output "    Get-PnpDeviceProperty -InstanceId '<instance>' -KeyName DEVPKEY_Device_Stack"
} else {
  Write-Output "?   $before 台から $after 台へ。一部だけ隠れました。"
}
Write-Output ""
Write-Output "元に戻すには: .\tools\hidhide-check.ps1 -Restore"
