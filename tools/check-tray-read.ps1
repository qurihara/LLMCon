# トレイに引っ込んだ LLMCon が、物理パッドを読み続けているかを確かめる道具。
#
# Issue #15 の修正（XInput の機体を XInputGetState で読む）が効いていることの、
# 最後の一押しの確認に使う。窓を最小化したうえで、実際にボタンを押して通るかを見る。
#
# 使い方:
#   .\tools\check-tray-read.ps1            最初の押下を待って記録する（既定 5 分待つ）
#   .\tools\check-tray-read.ps1 -WaitMinutes 30
#
# 読み方:
#   軸が 0.500 付近なら値が来ている。0.000 が並ぶなら来ていない（Issue #15 の症状）。
#   pressed: に番号が出れば、押下がトレイ常駐のまま通っている。
param(
  [int]$Port = 8788,
  [int]$WaitMinutes = 5
)
$ErrorActionPreference = "Stop"

Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class TrayCheck {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  public static string Fg() { IntPtr h = GetForegroundWindow(); var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
  public static void Minimize(IntPtr h) { ShowWindow(h, 6); }
}
"@ -ErrorAction SilentlyContinue

$U = "http://127.0.0.1:$Port/"
$H = @{"Content-Type"="application/json";"Accept"="application/json, text/event-stream"}
$r = Invoke-WebRequest -Uri $U -Method Post -Headers $H -Body (@{jsonrpc="2.0";id=1;method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="tray-check";version="1"}}}|ConvertTo-Json -Depth 10) -UseBasicParsing
$sid = $r.Headers["Mcp-Session-Id"]
$H2 = $H + @{"Mcp-Session-Id"=$sid}
Invoke-WebRequest -Uri $U -Method Post -Headers $H2 -Body (@{jsonrpc="2.0";method="notifications/initialized"}|ConvertTo-Json) -UseBasicParsing | Out-Null

function Call($n, $a) {
  $p = @{jsonrpc="2.0";id=2;method="tools/call";params=@{name=$n;arguments=$a}} | ConvertTo-Json -Depth 20
  $x = Invoke-WebRequest -Uri $U -Method Post -Headers $H2 -Body $p -UseBasicParsing
  $t = ""; foreach ($l in ($x.Content -split "`n")) { if ($l.StartsWith("data:")) { $t = $l.Substring(5).Trim() } }
  $o = $t | ConvertFrom-Json
  if ($o.result.content) { return ($o.result.content | ForEach-Object { $_.text }) -join "`n" }
  if ($o.error) { return "ERR: " + $o.error.message }
  return $t
}

Write-Output "=== list_pads ==="
Call "list_pads" @{}
Write-Output ""
Write-Output "パッドを選びます（0 番）"
Call "select_pad" @{ id = "0" }

# 窓をトレイへ引っ込める。ここからが本番の条件。
$proc = Get-Process CntlLevelConnection -ErrorAction SilentlyContinue
if ($proc -and $proc.MainWindowHandle -ne 0) {
  [TrayCheck]::Minimize($proc.MainWindowHandle)
  Start-Sleep -Seconds 2
}
Write-Output ""
Write-Output ("窓を最小化しました。いまの前面: " + [TrayCheck]::Fg())
Write-Output "この状態で、物理パッドのボタンを押してください。最大 $WaitMinutes 分待ちます。"
Write-Output ""

$deadline = (Get-Date).AddMinutes($WaitMinutes)
$sawPress = $false
$sawValues = $false
while ((Get-Date) -lt $deadline) {
  $raw = Call "get_pad_raw" @{}
  if ($raw -match 'a0=([\d.]+)') {
    $a0 = [double]$Matches[1]
    if ([Math]::Abs($a0 - 0.5) -lt 0.2) { $sawValues = $true }
  }
  if ($raw -match 'pressed:[ \t]*([^\r\n]*)' -and $Matches[1].Trim() -ne '-' -and $Matches[1].Trim() -ne '') {
    Write-Output ("[" + (Get-Date -Format "HH:mm:ss") + "] 押下を検出: " + $Matches[1].Trim())
    Write-Output ("  前面: " + [TrayCheck]::Fg())
    Write-Output $raw
    $sawPress = $true
    break
  }
  Start-Sleep -Milliseconds 150
}

Write-Output ""
Write-Output "=== 結果 ==="
if ($sawPress) {
  Write-Output "OK  トレイ常駐のまま押下が通りました。Issue #15 の修正は効いています。"
} elseif ($sawValues) {
  Write-Output "△  値は来ています（軸が中立 0.5）が、押下は観測できませんでした。押されなかっただけの可能性があります。"
} else {
  Write-Output "NG  軸が 0.000 のままです。トレイ常駐で値が来ていません（Issue #15 の症状）。"
}
