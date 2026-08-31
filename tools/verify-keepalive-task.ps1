# TailscaleKeepalive のタスクが SYSTEM として本当に動くのかを確認する。
# macbook-air へ届かない状態が続いているので、届く相手（qmbam3）で試して、
# タスクの仕組み自体が機能するのかを切り分ける。管理者権限で実行すること。
$ErrorActionPreference = 'Continue'
$tailscale = "C:\Program Files\Tailscale\tailscale.exe"

Write-Output "=== SYSTEM として tailscale ping が動くかを、届く相手(qmbam3)で試す ==="
$probe = "TailscaleKeepaliveProbe"
$old = Get-ScheduledTask -TaskName $probe -ErrorAction SilentlyContinue
if ($old) { Unregister-ScheduledTask -TaskName $probe -Confirm:$false }

$outFile = "C:\Windows\Temp\keepalive-probe.txt"
if (Test-Path $outFile) { Remove-Item $outFile -Force }

# cmd 経由で出力をファイルに落とし、SYSTEM から tailscale が使えるかを見る
$action = New-ScheduledTaskAction -Execute "cmd.exe" `
    -Argument "/c `"`"$tailscale`" ping -c 1 100.117.18.124 > $outFile 2>&1`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(1)
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $probe -Action $action -Trigger $trigger -Principal $principal | Out-Null
Start-ScheduledTask -TaskName $probe
Start-Sleep -Seconds 8
$info = Get-ScheduledTask -TaskName $probe | Get-ScheduledTaskInfo
Write-Output "probe LastResult: $($info.LastTaskResult)  (0 なら成功)"
if (Test-Path $outFile) {
    Write-Output "probe 出力:"
    Get-Content $outFile | ForEach-Object { Write-Output "  $_" }
} else {
    Write-Output "probe 出力ファイルが作られていない（SYSTEM から tailscale を実行できていない）"
}
Unregister-ScheduledTask -TaskName $probe -Confirm:$false

Write-Output ""
Write-Output "=== 本番のタスクの状態 ==="
$t = Get-ScheduledTask -TaskName "TailscaleKeepalive" -ErrorAction SilentlyContinue
if ($t) {
    $i = $t | Get-ScheduledTaskInfo
    Write-Output "State=$($t.State) LastRun=$($i.LastRunTime) LastResult=$($i.LastTaskResult) NextRun=$($i.NextRunTime)"
    Write-Output "（LastResult が 1 なのは、宛先の macbook-air へ届かず ping が失敗しているためと考えられる）"
}
Write-Output "完了"
