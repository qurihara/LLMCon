# -*- coding: utf-8 -*-
"""Bring Street Fighter 6 to the Training Mode fight screen before the Pomodoro timer starts (ROG Ally).

  1. checks that MCPCon answers (SF6 only sees MCPCon's virtual pad, so MCPCon must be up first);
  2. starts SF6 through Steam (steam://rungameid/1364780) if StreetFighter6.exe is not running;
  3. reads the screen (mss, or --frame-url of sf6/screen_server.py) and tells apart a few screens by the
     colours of fixed places, then presses one button through MCPCon's POST /connect/apply inject_tap:
        title screen (PRESS ANY BUTTON)            -> A   (about 10 s later the main menu appears)
        main menu                                  -> X   (Training Mode shortcut)
        "トレーニングモードを開始します。よろしいですか？" (はい selected) -> A   (about 5 s to Training Mode)
        Training Mode fight screen                 -> done (exit 0)
     The route was checked on the ROG Ally on 2026-11-19.

It never presses anything on a screen it cannot recognise (an online match, a lobby, a menu it does not
know): it waits a while (loading and intro screens), then stops and says so (exit 2). It never touches Casual
Match or any other online setting. When the Training Mode menu or BATTLE SETTING is open it stops as well
(exit 3) and does not press B, because B closes that menu and, if Casual Match was set to ON, starts online
matchmaking.

  python ensure_training.py                                  # on the ROG Ally
  python ensure_training.py --no-launch --frame-url http://127.0.0.1:8795/frame.png
  python ensure_training.py --classify a.png b.jpg           # only print what each picture is (tests)
Exit codes: 0 training mode reached, 1 MCPCon or SF6 not available, 2 unknown screen, 3 a menu is open,
4 timeout. Standard library only, plus mss for the screen (PIL is used when present, not required).
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from version import VERSION  # noqa: E402

IS_WIN = sys.platform.startswith("win")
SOURCE = "pomodoro-ensure"
SF6_EXE = "StreetFighter6.exe"
SF6_STEAM_APP_ID = 1364780  # Street Fighter 6 on Steam (store.steampowered.com/app/1364780)
GW, GH = 160, 90  # the screen is reduced to this grid before it is judged

# button to press on each screen, and how long to wait for the screen to change before pressing again
ACTIONS = {
    # 0.8.1: on the 2026-11-20 test A alone (3 times) did not leave PRESS ANY BUTTON; by hand Start then A did.
    # The title therefore gets Start and A in turn (see TITLE_BUTTONS); the game connects (about 10 s) after it.
    "title": ("A", 12.0),
    "main_menu": ("X", 4.0),
    "training_confirm": ("A", 10.0),  # about 5 s to Training Mode
}
TITLE_BUTTONS = ("Start", "A", "Start", "A")  # pressed in this order on the title screen, one per settle time
STOP_SCREENS = ("training_menu", "battle_setting")
WAIT_SCREENS = ("black",)


# -- the screen as a small grid of RGB tuples -------------------------------------------------------------
def grid_from_rgb(raw, w, h):
    """raw: bytes RGB, w*h*3 (mss shot.rgb or PIL tobytes). Each cell is the mean of 4 points inside it, so the
    result is close to a box-filtered thumbnail without PIL (pure Python, about 0.1 s for 1920x1080)."""
    rows = []
    for gy in range(GH):
        ys = [int((gy + fy) * h / GH) for fy in (0.25, 0.75)]
        row = []
        for gx in range(GW):
            xs = [int((gx + fx) * w / GW) for fx in (0.25, 0.75)]
            r = g = b = 0
            for y in ys:
                base = y * w * 3
                for x in xs:
                    i = base + x * 3
                    r += raw[i]
                    g += raw[i + 1]
                    b += raw[i + 2]
            row.append((r >> 2, g >> 2, b >> 2))
        rows.append(row)
    return rows


def grid_from_png_bytes(data):
    from PIL import Image  # only for --frame-url and the tests
    import io
    im = Image.open(io.BytesIO(data)).convert("RGB")
    return grid_from_rgb(im.tobytes(), im.size[0], im.size[1])


def grid_from_file(path):
    with open(path, "rb") as f:
        return grid_from_png_bytes(f.read())


def _region(g, x0, y0, x1, y1):
    return [g[y][x] for y in range(int(y0 * GH), int(y1 * GH)) for x in range(int(x0 * GW), int(x1 * GW))]


def _ratio(px, f):
    return sum(1 for p in px if f(p)) / float(len(px) or 1)


def _lum(p):
    return (p[0] * 299 + p[1] * 587 + p[2] * 114) / 1000.0


def _purple(p):
    return p[2] > 45 and p[0] > 25 and p[1] < p[2] * 0.6 and p[1] < p[0] * 0.8


def _navy(p):
    return p[2] > 40 and p[2] > p[0] * 1.4 and p[2] > p[1] * 1.15


def _gray(p):
    return 45 < _lum(p) < 140 and max(p) - min(p) < 25


def features(g):
    """Fixed places of the 1920x1080 (or 960x540) screen, as ratios. The top 6 % is left out: the ROG Ally's
    performance overlay may be drawn there; the bottom centre is left out: the Pomodoro overlay is there."""
    F = {}
    body = _region(g, 0, 0.06, 1, 1)
    F["lum"] = sum(_lum(p) for p in body) / len(body)
    # title: the white STREET FIGHTER letters across the middle on a dark screen
    F["title_letters"] = _ratio(_region(g, 0.25, 0.42, 0.75, 0.54), lambda p: _lum(p) > 170)
    # main menu: the magenta FIGHTING GROUND panel on the right, the dark logo in the centre
    F["right_purple"] = _ratio(_region(g, 0.70, 0.2, 1.0, 0.8), _purple)
    F["centre_dark"] = _ratio(_region(g, 0.3, 0.25, 0.6, 0.75), lambda p: _lum(p) < 40)
    # the Training Mode shortcut dialog (grey) with the selected "はい" button (white)
    F["dialog_grey"] = _ratio(_region(g, 0.27, 0.40, 0.70, 0.58), _gray)
    F["dialog_yes"] = _ratio(_region(g, 0.37, 0.585, 0.48, 0.62), lambda p: _lum(p) > 200)
    # Training Mode menu: purple panel over most of the screen
    F["menu_purple"] = _ratio(_region(g, 0.12, 0.15, 0.88, 0.85), _purple)
    # BATTLE SETTING: dark navy on the right side
    F["navy_right"] = _ratio(_region(g, 0.75, 0.25, 1.0, 0.9), _navy)
    # Training Mode fight: the damage panel under the health bars (ダメージ / コンボダメージ / 攻撃属性). On the
    # 160x90 grid its text lines are the rows 15, 17, 18 and 20 (y 0.17-0.23): dark on both sides of the centre
    # (x 0.41-0.48 and 0.53-0.59), while the stage beside the panel (x 0.26-0.33 and 0.675-0.74) and above it
    # (y 0.135-0.155) is bright. An online match has the round timer and no panel; a K.O., a VS screen or a
    # splash is not bright on both flanks.
    rows = (15, 17, 18, 20)
    panel = [g[y][x] for y in rows for x in list(range(66, 77)) + list(range(85, 95))]
    flanks = [g[y][x] for y in range(14, 22) for x in list(range(42, 53)) + list(range(108, 119))]
    above = [g[y][x] for y in (12, 13) for x in range(48, 112)]
    F["panel_dark"] = _ratio(panel, lambda p: _lum(p) < 90)
    F["panel_flanks_bright"] = _ratio(flanks, lambda p: _lum(p) > 150)
    F["panel_above_bright"] = _ratio(above, lambda p: _lum(p) > 150)
    return F


def classify(g):
    """One of: black, title, main_menu, training_confirm, training_menu, battle_setting, training, unknown."""
    F = features(g)
    if F["lum"] < 8:
        return "black", F
    if F["menu_purple"] > 0.7:
        return "training_menu", F
    if F["dialog_grey"] > 0.8 and F["dialog_yes"] > 0.3 and F["right_purple"] > 0.2:
        return "training_confirm", F
    if F["right_purple"] > 0.5 and F["centre_dark"] > 0.8 and F["dialog_grey"] < 0.2:
        return "main_menu", F
    if F["navy_right"] > 0.6 and F["menu_purple"] < 0.3 and F["lum"] < 90:
        return "battle_setting", F
    if F["lum"] < 60 and F["title_letters"] > 0.3 and F["right_purple"] < 0.1:
        return "title", F
    if F["panel_dark"] > 0.7 and F["panel_flanks_bright"] > 0.7 and F["panel_above_bright"] > 0.7:
        return "training", F
    return "unknown", F


# -- the machine --------------------------------------------------------------------------------------------
def http_up(url, timeout=2):
    try:
        urllib.request.urlopen(url, timeout=timeout).read()
        return True
    except urllib.error.HTTPError:
        return True  # an error page also means that something answers
    except Exception:
        return False


def sf6_pid():
    if not IS_WIN:
        return None
    try:
        out = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq %s" % SF6_EXE, "/FO", "CSV", "/NH"],
                                      creationflags=0x08000000).decode("utf-8", "replace")
    except Exception:
        return None
    for line in out.splitlines():
        cells = [c.strip('"') for c in line.split('","')]
        if len(cells) > 1 and cells[0].lower() == SF6_EXE.lower():
            try:
                return int(cells[1])
            except ValueError:
                pass
    return None


def bring_to_front(pid):
    """Give SF6 the focus (SF6 ignores the pad and drops to 2 fps when another window has it)."""
    if not IS_WIN or not pid:
        return False
    import ctypes
    from ctypes import wintypes
    user32 = ctypes.windll.user32
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd) and not user32.GetWindow(hwnd, 4):  # GW_OWNER
            found.append(hwnd)
        return True
    user32.EnumWindows(cb, 0)
    for h in found:
        user32.SetForegroundWindow(h)
        return True
    return False


class Grabber(object):
    def __init__(self, frame_url=None):
        self.frame_url = frame_url
        self.sct = None

    def grab(self):
        if self.frame_url:
            with urllib.request.urlopen(self.frame_url, timeout=5) as r:
                return grid_from_png_bytes(r.read())
        if self.sct is None:
            import mss
            self.sct = mss.MSS() if hasattr(mss, "MSS") else mss.mss()
        mon = self.sct.monitors[1]
        shot = self.sct.grab(mon)
        return grid_from_rgb(shot.rgb, shot.size[0], shot.size[1])


def tap(mcpcon, button, frames=5, dry_run=False):
    payload = {"kind": "inject_tap", "buttons": [button], "frames": frames, "from": SOURCE}
    if dry_run:
        return
    req = urllib.request.Request(mcpcon.rstrip("/") + "/connect/apply", data=json.dumps(payload).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    urllib.request.urlopen(req, timeout=3).read()


def log(msg, **kw):
    print("[%s] %s %s" % (time.strftime("%H:%M:%S"), msg, json.dumps(kw, ensure_ascii=False) if kw else ""),
          flush=True)


def ensure(a):
    if not http_up(a.mcpcon):
        log("NG: MCPCon does not answer", mcpcon=a.mcpcon)
        return 1
    launched = False
    pid = sf6_pid()
    if IS_WIN and pid is None:
        if a.no_launch:
            log("NG: SF6 is not running (--no-launch)")
            return 1
        log("SF6 is not running; starting it through Steam", app=SF6_STEAM_APP_ID)
        os.startfile("steam://rungameid/%d" % SF6_STEAM_APP_ID)
        launched = True
        t_end = time.time() + a.launch_timeout
        while pid is None and time.time() < t_end:
            time.sleep(2)
            pid = sf6_pid()
        if pid is None:
            log("NG: SF6 did not start", waited_sec=a.launch_timeout)
            return 1
        log("SF6 started", pid=pid)
    grab = Grabber(a.frame_url)
    # intro movies and loading screens are not recognised; wait longer for them after a launch
    unknown_limit = a.unknown_after_launch if launched else a.unknown_sec
    t_end = time.time() + a.timeout
    prev, same, unknown_since = None, 0, None
    pressed = {}  # screen -> (count, time of the last press)
    first_seen = {}  # screen -> time it was first recognised (the title is left alone a few seconds first)
    while time.time() < t_end:
        try:
            g = grab.grab()
        except Exception as e:
            log("screen capture failed", error=str(e))
            time.sleep(1)
            continue
        screen, _ = classify(g)
        same = same + 1 if screen == prev else 1
        if screen != prev:
            log("screen", screen=screen)
        prev = screen
        if same < 2:  # act only on a screen seen twice in a row (not on a frame in the middle of a fade)
            time.sleep(a.poll)
            continue
        if screen == "unknown":
            unknown_since = unknown_since or time.time()
            if time.time() - unknown_since > unknown_limit:
                log("STOP: the screen is not one I know; nothing was pressed. Please bring SF6 to the Training "
                    "Mode fight screen by hand.", waited_sec=round(time.time() - unknown_since, 1))
                return 2
            time.sleep(a.poll)
            continue
        unknown_since = None
        if screen == "training":
            log("OK: SF6 is on the Training Mode fight screen")
            return 0
        if screen in STOP_SCREENS:
            log("STOP: %s is open. Nothing was pressed (B would close it and could start Casual Match "
                "matchmaking). Please close it by hand." % screen)
            return 3
        if screen in WAIT_SCREENS:
            time.sleep(a.poll)
            continue
        button, settle = ACTIONS[screen]
        n, last = pressed.get(screen, (0, 0.0))
        first_seen.setdefault(screen, time.time())
        if screen == "title" and time.time() - first_seen[screen] < a.title_wait:
            time.sleep(a.poll)  # right after the launch the title may not take input yet
            continue
        if time.time() - last < settle:
            time.sleep(a.poll)
            continue
        limit = len(TITLE_BUTTONS) if screen == "title" else 3
        if screen == "title" and n < limit:
            button = TITLE_BUTTONS[n]
        if n >= limit:
            log("STOP: pressed %d times on %s and the screen did not change" % (n, screen))
            return 2
        if a.focus:
            bring_to_front(pid)
        try:
            tap(a.mcpcon, button, dry_run=a.dry_run)
        except Exception as e:
            log("NG: inject_tap failed", error=str(e))
            return 1
        pressed[screen] = (n + 1, time.time())
        log("pressed", screen=screen, button=button, n=n + 1)
        time.sleep(a.poll)
    log("STOP: timeout", sec=a.timeout)
    return 4


def main(argv=None):
    ap = argparse.ArgumentParser(description="ensure_training (Street Pomodoro Timer %s)" % VERSION)
    ap.add_argument("--mcpcon", default="http://127.0.0.1:8777/")
    ap.add_argument("--frame-url", default=None, help="take the screen from sf6/screen_server.py instead of mss")
    ap.add_argument("--no-launch", action="store_true", help="do not start SF6 when it is not running")
    ap.add_argument("--no-focus", dest="focus", action="store_false", help="do not bring SF6 to the front")
    ap.add_argument("--dry-run", action="store_true", help="judge the screen but press nothing")
    ap.add_argument("--timeout", type=float, default=240.0)
    ap.add_argument("--launch-timeout", type=float, default=90.0, help="seconds to wait for StreetFighter6.exe")
    ap.add_argument("--unknown-sec", type=float, default=20.0, help="give up after an unknown screen this long")
    ap.add_argument("--unknown-after-launch", type=float, default=120.0,
                    help="the same right after starting SF6 (intro movies, notices)")
    ap.add_argument("--poll", type=float, default=0.5)
    ap.add_argument("--title-wait", type=float, default=6.0,
                    help="seconds to leave the title screen alone after it is first recognised")
    ap.add_argument("--classify", nargs="+", default=None, metavar="PICTURE",
                    help="only print what each picture is, then exit")
    a = ap.parse_args(argv)
    if a.classify:
        for p in a.classify:
            screen, F = classify(grid_from_file(p))
            print("%-18s %s  %s" % (screen, p, " ".join("%s=%.2f" % kv for kv in sorted(F.items()))))
        return 0
    return ensure(a)


if __name__ == "__main__":
    sys.exit(main())
