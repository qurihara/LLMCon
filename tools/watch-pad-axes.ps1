# 物理パッドの生の軸を、しばらく見つづけて、どの軸が動いたかを報告する道具。
# パッドを操作しながらこれを走らせると、どの軸がどの操作に当たるかが分かる。
# 判明した番号は set_pad_axes に渡す。
#
# 使い方:
#   .\tools\watch-pad-axes.ps1                       既定（8777、10秒）
#   .\tools\watch-pad-axes.ps1 -Seconds 15 -Pad 1    パッド[1]を15秒見る
#   .\tools\watch-pad-axes.ps1 -Label "右スティックを回す"
param(
  [int]$Port = 8777,
  [int]$Seconds = 10,
  [string]$Pad = "",       # list_pads の番号。空なら選択中のパッド
  [string]$Label = ""
)
$ErrorActionPreference = "Stop"

function New-Session {
  param([int]$P)
  $b = @{jsonrpc="2.0";id=1;method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="watch";version="1"}}} | ConvertTo-Json -Depth 10
  $r = Invoke-WebRequest -Uri "http://127.0.0.1:$P/" -Method Post -Headers @{"Content-Type"="application/json";"Accept"="application/json, text/event-stream"} -Body $b -UseBasicParsing
  $sid = $r.Headers["Mcp-Session-Id"]
  Invoke-WebRequest -Uri "http://127.0.0.1:$P/" -Method Post -Headers @{"Content-Type"="application/json";"Accept"="application/json, text/event-stream";"Mcp-Session-Id"=$sid} -Body (@{jsonrpc="2.0";method="notifications/initialized"}|ConvertTo-Json) -UseBasicParsing | Out-Null
  return $sid
}
function Invoke-Tool {
  param([int]$P, [string]$Sid, [string]$ToolName, [hashtable]$ToolArgs)
  $payload = @{jsonrpc="2.0";id=2;method="tools/call";params=@{name=$ToolName;arguments=$ToolArgs}} | ConvertTo-Json -Depth 20
  $r = Invoke-WebRequest -Uri "http://127.0.0.1:$P/" -Method Post -Headers @{"Content-Type"="application/json";"Accept"="application/json, text/event-stream";"Mcp-Session-Id"=$Sid} -Body $payload -UseBasicParsing
  $t = ""
  foreach ($l in ($r.Content -split "`n")) { if ($l.StartsWith("data:")) { $t = $l.Substring(5).Trim() } }
  $o = $t | ConvertFrom-Json
  if ($o.result -and $o.result.content) { return ($o.result.content | ForEach-Object { $_.text }) -join "`n" }
  if ($o.error) { return "ERR: " + $o.error.message }
  return $t
}

$S = New-Session -P $Port
if ($Pad -ne "") { Invoke-Tool -P $Port -Sid $S -ToolName "select_pad" -ToolArgs @{ id = $Pad } | Write-Output }

if ($Label -ne "") { Write-Output ("▼ " + $Label) }
Write-Output ("$Seconds 秒間、軸の動きを見ます。いまパッドを操作してください。")

$min = @{}; $max = @{}; $samples = 0
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  $raw = Invoke-Tool -P $Port -Sid $S -ToolName "get_pad_raw" -ToolArgs @{}
  foreach ($m in [regex]::Matches($raw, 'a(\d+)=([-\d.]+)')) {
    $i = [int]$m.Groups[1].Value
    $v = [double]$m.Groups[2].Value
    if (-not $min.ContainsKey($i) -or $v -lt $min[$i]) { $min[$i] = $v }
    if (-not $max.ContainsKey($i) -or $v -gt $max[$i]) { $max[$i] = $v }
  }
  $samples++
  Start-Sleep -Milliseconds 60
}

Write-Output ""
Write-Output ("観測 $samples 回。軸ごとの動いた範囲:")
foreach ($i in ($min.Keys | Sort-Object)) {
  $range = $max[$i] - $min[$i]
  $moved = if ($range -gt 0.15) { "  ← 動いた" } else { "" }
  Write-Output ("  a{0}: {1:F3} .. {2:F3}   幅 {3:F3}{4}" -f $i, $min[$i], $max[$i], $range, $moved)
}
Write-Output ""
Write-Output "読み方: スティックは中央がおよそ 0.5 で両側へ振れます。トリガーは 0 から上へ動きます。"
Write-Output "判明した番号は set_pad_axes に渡してください（例 lx=0 ly=1 rx=2 ry=3 lt=4 rt=5）。"
