# Issue #4 への対処。mac と Windows 機のあいだの Tailscale の経路が数分で切れる問題を、
# ファイアウォールの規則と、定期的な通信の仕掛けで安定させる。管理者権限で実行すること。
#
# 背景（調査で分かったこと）:
#   物理のネットワーク qlab-A は Public に分類されている（研究室の共有ネットワークなので、
#   Issue の指示どおり Private には変えない）。ところが Tailscale が入れている規則は
#     Tailscale-Process : tailscaled.exe への着信を許可（ただし UDP だけ）／プロファイルは Any
#     Tailscale-In      : 全プロトコルを許可／プロファイルは Domain と Private だけ
#   となっており、Public では tailscaled.exe への UDP しか通らない。直接の経路の確立や
#   中継とのやり取りに使う TCP が Public で遮られるため、経路が失われたまま復活しにくい。
#
# 対処:
#   1. tailscaled.exe への着信を、全プロトコル・全プロファイル（Public を含む）で許可する。
#   2. mac へ2分おきに通信を送るタスクを登録する。研究室のネットワークが NAT の割り当てを
#      短時間で捨てている可能性への対処であり、利用者がログオンしていなくても動くようにする。

$ErrorActionPreference = 'Stop'

$tailscaled = "C:\Program Files\Tailscale\tailscaled.exe"
$tailscale  = "C:\Program Files\Tailscale\tailscale.exe"
$macAddr    = "100.69.63.106"

if (-not (Test-Path $tailscaled)) { throw "tailscaled.exe が見つかりません: $tailscaled" }
if (-not (Test-Path $tailscale))  { throw "tailscale.exe が見つかりません: $tailscale" }

Write-Output "=== 1. ファイアウォール: tailscaled.exe への着信を全プロトコル・全プロファイルで許可 ==="
$ruleName = "Tailscale (tailscaled) all profiles"
$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output "既に存在するので作り直す"
    Remove-NetFirewallRule -DisplayName $ruleName
}
New-NetFirewallRule -DisplayName $ruleName `
    -Direction Inbound -Program $tailscaled -Action Allow -Profile Any -Protocol Any | Out-Null
Write-Output "作成した: $ruleName"

Write-Output ""
Write-Output "=== 2. タスクスケジューラ: 2分おきに mac へ通信を送る ==="
$taskName = "TailscaleKeepalive"
$old = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($old) {
    Write-Output "既に存在するので作り直す"
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
# tailscale ping は経路の確立そのものを促すので、これを使う。出力は捨てる。
$action = New-ScheduledTaskAction -Execute $tailscale -Argument "ping -c 1 $macAddr"
# 繰り返しの期間に [TimeSpan]::MaxValue を渡すと、タスクスケジューラが受け付ける範囲を超えて
# 登録に失敗する（P99999999DT23H59M59S が範囲外と言われる）。実用上は十分に長い有限の値にする。
# 起動時のトリガーも足してあるので、再起動のたびに期間は数え直される。
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 2) -RepetitionDuration (New-TimeSpan -Days 365)
# 起動時にも動くようにして、利用者のログオンに依存しないようにする
$bootTrigger = New-ScheduledTaskTrigger -AtStartup
$bootTrigger.Repetition = $trigger.Repetition
# SYSTEM として動かす（ログオンしていなくても動く）。ウィンドウは出さない。
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($trigger, $bootTrigger) `
    -Principal $principal -Settings $settings `
    -Description "mac (100.69.63.106) との Tailscale の経路を維持する。Issue #4 の対処。" | Out-Null
Write-Output "登録した: $taskName（2分おき、SYSTEM として実行、起動時にも実行）"

Start-ScheduledTask -TaskName $taskName
Write-Output "いま一度実行した"

Write-Output ""
Write-Output "=== 結果 ==="
Get-NetFirewallRule -DisplayName "*ailscale*" |
    Select-Object DisplayName, Enabled, Direction, Action, Profile | Format-Table -AutoSize | Out-String | Write-Output
Get-ScheduledTask -TaskName $taskName |
    Select-Object TaskName, State | Format-Table -AutoSize | Out-String | Write-Output
Write-Output "完了"
