# Issue #4 の対処の状態を、管理者権限で確認する。あわせて経路の回復も試みる。
$ErrorActionPreference = 'Continue'
$tailscale = "C:\Program Files\Tailscale\tailscale.exe"

Write-Output "=== 1. ファイアウォールの規則 ==="
Get-NetFirewallRule -DisplayName "*ailscale*" -ErrorAction SilentlyContinue |
    Select-Object DisplayName, Enabled, Direction, Action, Profile |
    Format-Table -AutoSize | Out-String | Write-Output

Write-Output "=== 2. タスクの存在 ==="
$t = Get-ScheduledTask -TaskName "TailscaleKeepalive" -ErrorAction SilentlyContinue
if ($t) {
    $i = $t | Get-ScheduledTaskInfo
    Write-Output "State        : $($t.State)"
    Write-Output "LastRunTime  : $($i.LastRunTime)"
    Write-Output "LastResult   : $($i.LastTaskResult)"
    Write-Output "NextRunTime  : $($i.NextRunTime)"
    Write-Output "Principal    : $($t.Principal.UserId) / $($t.Principal.LogonType)"
    foreach ($tr in $t.Triggers) {
        Write-Output ("Trigger      : {0} repetition={1} duration={2}" -f $tr.CimClass.CimClassName, $tr.Repetition.Interval, $tr.Repetition.Duration)
    }
    foreach ($ac in $t.Actions) { Write-Output "Action       : $($ac.Execute) $($ac.Arguments)" }
} else {
    Write-Output "タスクが存在しない"
}

Write-Output ""
Write-Output "=== 3. 経路の回復を試みる（ping を数回）==="
for ($i = 1; $i -le 5; $i++) {
    $out = & $tailscale ping -c 1 100.69.63.106 2>&1 | Out-String
    Write-Output ("try {0}: {1}" -f $i, $out.Trim())
    Start-Sleep -Seconds 2
}

Write-Output ""
Write-Output "=== 4. 相手の状態 ==="
$json = & $tailscale status --json 2>&1 | ConvertFrom-Json
$json.Peer.PSObject.Properties | ForEach-Object {
    $p = $_.Value
    if ($p.HostName -match "acBook|macbook") {
        Write-Output "HostName     : $($p.HostName)"
        Write-Output "Online       : $($p.Online)"
        Write-Output "CurAddr      : [$($p.CurAddr)]   (直接の経路。空なら直接つながっていない)"
        Write-Output "Relay        : [$($p.Relay)]     (中継。空なら中継の割り当ても無い)"
        Write-Output "LastHandshake: $($p.LastHandshake)"
        Write-Output "Rx/Tx        : $($p.RxBytes)/$($p.TxBytes)"
    }
}
Write-Output "完了"
