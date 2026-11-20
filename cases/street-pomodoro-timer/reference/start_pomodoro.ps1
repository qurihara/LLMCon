# Street Pomodoro Timer: start everything on the ROG Ally in one go (the player runs this).
#
#   powershell -ExecutionPolicy Bypass -File start_pomodoro.ps1            # offline: no online matchmaking
#   powershell -ExecutionPolicy Bypass -File start_pomodoro.ps1 -Online    # start Casual Match at the end of work
#   powershell -ExecutionPolicy Bypass -File start_pomodoro.ps1 -Stop      # stop the timer and the overlay
#   ... -EnsureTraining   first start SF6 if needed and bring it to the Training Mode fight screen (ensure_training.py)
#   ... -Listen           serve the timer's control page and HTTP API on the Tailscale address (<TAILSCALE_ADDRESS>:8810)
#                         so that the lab's Mac (a browser or an LLM with curl) can pause, resume, reset, stop and configure it
#   ... -NoVolume         do not lower SF6's own volume during work
#
# What it does:
#   1. checks that MCPCon answers on 127.0.0.1:8777 (starts it if not; then SF6 must be restarted, because SF6
#      keeps the controllers it saw when it started);
#   2. runs setup_pad.py (MCPCon reads the arcade stick pws and gets its axes; MCPCon forgets both on restart);
#   3. starts pomodoro.py (with --restrict, and --allow-online only with -Online) and overlay.py without any
#      console window, because a console window in front of SF6 takes its focus and SF6 drops to 2 fps;
#   4. brings SF6 back to the front.
# With -EnsureTraining, step 2.5 runs ensure_training.py: it starts SF6 through Steam when it is not running and
# presses A / X / A (title, main menu, Training Mode shortcut) until the Training Mode fight screen; on a screen it
# does not know it presses nothing and the timer is not started.
param(
    [switch]$Online,
    [switch]$Stop,
    [switch]$EnsureTraining,
    [switch]$Listen,
    [switch]$NoVolume,
    [string]$ListenAddress = "",   # e.g. your Tailscale address
    [double]$Work = 25,
    [double]$Break = 5,
    [string]$Python = "python",   # a Python 3 with tkinter, mss and (optional) pycaw
    [string]$McpconExe = ""   # path to CntlLevelConnection.exe (LLMCon), used when MCPCon is not running
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$pythonw = Join-Path (Split-Path -Parent $Python) "pythonw.exe"

function Stop-Pomodoro {
    # pomodoro.py and overlay.py (the venv's pythonw starts the base pythonw as a child, so match by command line)
    Get-CimInstance Win32_Process | Where-Object {
        $_.Name -like "python*" -and $_.CommandLine -and
        ($_.CommandLine -match "pomodoro\.py" -or $_.CommandLine -match "overlay\.py")
    } | ForEach-Object { Write-Host "stop PID $($_.ProcessId): $($_.CommandLine)"; Stop-Process -Id $_.ProcessId -Force }
}

function Request-TimerStop {
    # 0.8.3: ask pomodoro.py to stop by itself first, so that it puts SF6's volume back to 100% (a forced stop
    # left it at 30%). If the timer does not answer, the forced stop below still runs.
    try {
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Method Post -Uri "http://127.0.0.1:8810/stop" `
            -ContentType "application/json" -Body '{"from":"start_pomodoro.ps1"}' | Out-Null
        Write-Host "asked the timer to stop (SF6 volume back to 100%)."
        Start-Sleep -Seconds 2
    } catch { Write-Host "the timer did not answer POST /stop; stopping it by force." }
}

if ($Stop) { Request-TimerStop; Stop-Pomodoro; Write-Host "stopped."; exit 0 }

# 1. MCPCon
function Test-Mcpcon {
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 -Uri "http://127.0.0.1:8777/" | Out-Null; return $true }
    catch { if ($_.Exception.Response) { return $true } else { return $false } }   # 400 also means it is up
}
$mcpconStarted = $false
if (-not (Test-Mcpcon)) {
    $mcpconStarted = $true
    Write-Host "MCPCon is not running; starting it."
    if (-not $McpconExe) { Write-Host "NG: MCPCon is not running; start it or pass -McpconExe <path to CntlLevelConnection.exe>."; exit 1 }
    $bind = if ($ListenAddress) { $ListenAddress } else { "127.0.0.1" }
    Start-Process -FilePath $McpconExe -ArgumentList "--port", "8777", "--bind", $bind -WindowStyle Minimized
    for ($i = 0; $i -lt 30 -and -not (Test-Mcpcon); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Mcpcon)) { Write-Host "NG: MCPCon did not start."; exit 1 }
    Write-Host "MCPCon started. If SF6 was already running, restart SF6 so that it sees MCPCon's virtual pad."
}

# 2. the arcade stick
Push-Location $here
& $Python setup_pad.py --mcpcon http://127.0.0.1:8777/
if ($LASTEXITCODE -ne 0) { Pop-Location; Write-Host "NG: setup_pad.py failed."; exit 1 }

# 2.5 SF6 to the Training Mode fight screen
if ($EnsureTraining) {
    if ($mcpconStarted -and (Get-Process -Name "StreetFighter6" -ErrorAction SilentlyContinue)) {
        Pop-Location
        Write-Host "NG: MCPCon was started just now while SF6 was running; SF6 does not see MCPCon's pad. Quit SF6 and run this again (SF6 is then started by -EnsureTraining)."
        exit 1
    }
    & $Python ensure_training.py --mcpcon http://127.0.0.1:8777/
    if ($LASTEXITCODE -ne 0) {
        Pop-Location
        Write-Host "NG: SF6 is not on the Training Mode fight screen (ensure_training.py exit $LASTEXITCODE); the timer was not started."
        exit 1
    }
}

# 3. timer and overlay, without console windows (stop old ones first)
Request-TimerStop
Stop-Pomodoro
$pargs = @("pomodoro.py", "--restrict", "--work", "$Work", "--break", "$Break", "--mcpcon", "http://127.0.0.1:8777/")
if ($Online) { $pargs += "--allow-online" }
if ($Listen) { $pargs += @("--host", "127.0.0.1,$ListenAddress") }
if ($NoVolume) { $pargs += "--no-volume" }
Start-Process -FilePath $Python -ArgumentList $pargs -WindowStyle Hidden -WorkingDirectory $here
Start-Process -FilePath $pythonw -ArgumentList "overlay.py" -WorkingDirectory $here
Pop-Location
if ($Online) { Write-Host "timer started (Casual Match starts at the end of each work period)." }
else { Write-Host "timer started (offline: the menu steps are not sent)." }
Write-Host "control page: http://127.0.0.1:8810/"
if ($Listen) { Write-Host "control page from other machines: http://${ListenAddress}:8810/ (Tailscale)" }

# 4. SF6 to the front. The overlay window may appear several seconds after it is started (0.8.0 and later);
#    if it takes the foreground, SF6 in the background ignores the pad (2026-11-20). So bring SF6 to the front
#    three times over about 8 seconds (overlay.py also gives the foreground back to SF6 by itself since 0.8.2).
$sf6 = Get-Process -Name "StreetFighter6" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($sf6) {
    Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'
    foreach ($wait in 2, 3, 3) {
        Start-Sleep -Seconds $wait
        [W.U]::SetForegroundWindow($sf6.MainWindowHandle) | Out-Null
    }
    Write-Host "SF6 brought to the front."
} else { Write-Host "SF6 is not running." }
