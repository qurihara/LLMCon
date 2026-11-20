# -*- coding: utf-8 -*-
"""Street Pomodoro Timer overlay: a small countdown drawn over the bottom centre of the Street Fighter 6 screen,
with beeps and spoken cues. Runs on the ROG Ally (Windows) next to pomodoro.py and reads its state from
http://127.0.0.1:8810/state every 0.25 s.

The window must never take the focus from SF6: when a console window covered SF6 in hadoken-slides, SF6 fell to
2 frames per second and ignored the injected input. The window is therefore borderless, always on top, with a
transparent background, and has the Windows extended styles WS_EX_NOACTIVATE (never activated),
WS_EX_TRANSPARENT (mouse clicks pass through), WS_EX_TOOLWINDOW (not in the task bar or Alt+Tab) and
WS_EX_LAYERED. The ROG Ally's own performance overlay is drawn the same way over SF6 and SF6 keeps 60 fps
(Kurihara, 2026-11-19).

Look (0.8.0, Kurihara chose "plan A" on 2026-11-20): tkinter's transparent colour key cannot make the
anti-aliased edge pixels of a text transparent, so text drawn straight on the key colour had jagged edges.
Every text now sits on an opaque dark rounded panel (a smoothed polygon on a Canvas), and only the area outside
the panels has the key colour; the anti-aliased edges blend into the dark panel. The digits use Bahnschrift
(Segoe UI when it is missing), the words Yu Gothic UI.

Sounds (Windows only; standard library only):
  first work   : "作業を始めます。25分です" (R2; after a reset "作業をやり直します。25分です")
  work / prep  : "作業はあと5分です" at 5 min, "あと1分" at 1 min, "10秒前" at 10 s, a beep each second from 5 s,
                 a long beep and "休憩です" at 0.
  break        : "休憩はあと1分です" at 1 min, "10秒前" at 10 s, beeps from 5 s, a long beep and
                 "休憩終わり。作業に戻りましょう" at 0.
  restrictions : "操作を難しくします。<name>" when a level goes up, "制限を解除しました" when they end.
  pause        : "一時停止しました" / "再開します".
Every sentence is played from a WAV made beforehand with VOICEVOX (ずんだもん; tools/make_voice.py writes
voice/*.wav and voice/index.json) through winsound.PlaySound. A sentence that is not in voice/index.json is
spoken by Windows System.Speech (PowerShell, started with CREATE_NO_WINDOW so that no console window appears).
One worker plays the sentences one after another; a sentence that waited more than 8 s is dropped.

  python overlay.py                       # bottom centre of the screen
  python overlay.py --corner top-right --scale 1.2 --no-voice
  python overlay.py --state http://127.0.0.1:8810/state --say-test
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from version import VERSION  # noqa: E402

IS_WIN = sys.platform.startswith("win")
CREATE_NO_WINDOW = 0x08000000

NAMES = {"work": "作業中", "prep": "作業中", "break": "休憩中", "idle": "待機中", "stopped": "停止"}
COLORS = {"work": "#7fd3ff", "prep": "#7fd3ff", "break": "#ffb347", "idle": "#cccccc", "stopped": "#cccccc"}
RED = "#ff5050"
RESTRICT_COLOR = "#ff7070"
KEY = "#010203"      # the colour made transparent (outside the panels)
PANEL = "#15181c"    # the opaque dark panel behind every text (Kurihara: text must read over a bright floor)
PANEL_EDGE = "#2b3038"

# -- the sentences ----------------------------------------------------------------------------------------
S_WORK_5MIN = "作業はあと5分です"
S_1MIN = "あと1分"
S_10SEC = "10秒前"
S_BREAK = "休憩です"
S_BREAK_1MIN = "休憩はあと1分です"
S_BACK_TO_WORK = "休憩終わり。作業に戻りましょう"
S_LIFTED = "制限を解除しました"
S_MENU = "メニューを開いたので、しばらく制限を外しています"
S_PAUSE = "一時停止しました"
S_RESUME = "再開します"
S_HARDER = "操作を難しくします。%s"
S_START = "作業を始めます。%s"
S_RESTART = "作業をやり直します。%s"
S_TEST = "ストリート ポモドーロ タイマーの読み上げの試験です"
S_PREP_SKIPPED = "トレーニングモードではないので、対戦の準備をやめました"


def duration_text(sec):
    """'25分です', '30秒です', '1分30秒です'."""
    sec = int(round(float(sec)))
    m, s = divmod(sec, 60)
    if m and s:
        return "%d分%d秒です" % (m, s)
    if m:
        return "%d分です" % m
    return "%d秒です" % s


# work lengths (minutes) whose "作業を始めます。N分です" has a WAV; all 1..60 were 12 MB, so the common ones only
VOICE_MINUTES = tuple(range(1, 11)) + (15, 20, 25, 30, 40, 45, 50, 55, 60)


def all_sentences(restrict_labels, minutes=VOICE_MINUTES):
    """Every sentence this overlay says (the work length only for VOICE_MINUTES), for tools/make_voice.py and the
    test; any other work length is spoken by System.Speech."""
    out = [S_WORK_5MIN, S_1MIN, S_10SEC, S_BREAK, S_BREAK_1MIN, S_BACK_TO_WORK, S_LIFTED, S_MENU, S_PAUSE,
           S_RESUME, S_TEST, S_PREP_SKIPPED]
    out += [S_HARDER % label for label in restrict_labels]
    for m in minutes:
        out += [S_START % duration_text(m * 60), S_RESTART % duration_text(m * 60)]
    return out


# -- speech -----------------------------------------------------------------------------------------------
RATE = {"value": -2}  # System.Speech rate, -10 (slow) .. 10 (fast); 1 was too fast to follow (Kurihara)
SPEECH_MAX_AGE = 8.0  # seconds; a sentence that waited longer than this in the queue is dropped (out of date)
VOICE_DIR = os.path.join(HERE, "voice")
_speech_q = None
_speech_lock = threading.Lock()
_voice = {"index": None}


def voice_index():
    """sentence -> WAV path, from voice/index.json (made by tools/make_voice.py)."""
    if _voice["index"] is None:
        idx = {}
        try:
            with open(os.path.join(VOICE_DIR, "index.json"), encoding="utf-8") as f:
                for text, name in json.load(f)["sentences"].items():
                    path = os.path.join(VOICE_DIR, name)
                    if os.path.exists(path):
                        idx[text] = path
        except (OSError, ValueError, KeyError) as e:
            print("[voice] no voice/index.json (%s); System.Speech is used" % e, flush=True)
        _voice["index"] = idx
    return _voice["index"]


def _speak_now(text):
    wav = voice_index().get(text)
    if wav and IS_WIN:
        try:
            import winsound
            winsound.PlaySound(wav, winsound.SND_FILENAME)  # synchronous: one sentence at a time
            return
        except Exception as e:
            print("[wav failed]", e, flush=True)
    ps = ("Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
          "try { $s.SelectVoiceByHints('NotSet', 'NotSet', 0, [System.Globalization.CultureInfo]'ja-JP') } catch {}; "
          "$s.Rate = %d; $s.Speak('%s')" % (RATE["value"], text.replace("'", "")))
    try:
        p = subprocess.Popen(["powershell", "-NoProfile", "-WindowStyle", "Hidden", "-Command", ps],
                             creationflags=CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        p.wait(timeout=30)  # one sentence at a time: wait until it has been spoken (Kurihara: voices overlapped)
    except Exception as e:
        print("[say failed]", e, flush=True)


def _speech_worker():
    while True:
        queued_at, text = _speech_q.get()
        if time.time() - queued_at > SPEECH_MAX_AGE:
            print("[say dropped, too old]", text, flush=True)
            continue
        _speak_now(text)


def say(text, voice=True):
    """Queue a sentence. A single worker speaks the sentences one after another, never on top of each other."""
    global _speech_q
    if not voice or not IS_WIN:
        print("[say]", text, "(wav)" if text in voice_index() else "(System.Speech)", flush=True)
        return
    import queue
    with _speech_lock:
        if _speech_q is None:
            _speech_q = queue.Queue()
            threading.Thread(target=_speech_worker, daemon=True).start()
    _speech_q.put((time.time(), text))


def beep(freq=880, ms=150):
    if IS_WIN:
        import winsound
        threading.Thread(target=winsound.Beep, args=(freq, ms), daemon=True).start()
    else:
        print("[beep %d %d]" % (freq, ms), flush=True)


class Announcer(object):
    """Turns the state into beeps and speech, each cue once per phase."""

    def __init__(self, voice=True, status=None, say_fn=None, beep_fn=None):
        self.voice = voice
        # status(text, seconds): show a temporary status line above the timer (Kurihara, 2026-11-19)
        self.status = status or (lambda text, sec=6: print("[status]", text, flush=True))
        self._say = say_fn or (lambda text: say(text, self.voice))
        self._beep = beep_fn or beep
        self.key = None   # (cycle, phase kind, work_seq) of the phase being announced
        self.done = set()
        self.work_seq = None
        self.paused = None
        self.r_level, self.r_susp = 0, False
        self.prep_check = None

    def update(self, s):
        phase, rem = s.get("phase"), s.get("remaining_sec")
        kind = "work" if phase in ("work", "prep") else phase
        self._work_start(s, kind, rem)
        self._pause(s)
        self._prep_check(s)
        key = (s.get("cycle"), kind, s.get("work_seq"))
        if key != self.key:
            self.key, self.done = key, set()
        if rem is None or kind not in ("work", "break") or s.get("paused"):
            return
        if kind == "work":
            cues = [(300, lambda: self._say(S_WORK_5MIN)), (60, lambda: self._say(S_1MIN))]
        else:
            cues = [(60, lambda: self._say(S_BREAK_1MIN))]
        cues.append((10, lambda: self._say(S_10SEC)))
        for n in (5, 4, 3, 2, 1):
            cues.append((n, lambda: self._beep(880, 120)))
        for at, fn in cues:
            # fire when the remaining time crosses the cue, but not for cues long past (late start)
            if at not in self.done and rem <= at and rem > at - 2:
                self.done.add(at)
                fn()
            elif at not in self.done and rem <= at - 2:
                self.done.add(at)

    def _work_start(self, s, kind, rem):
        """R2: say the length of the work period when the first one starts (or when a reset starts one again).
        After a break, phase_changed says "休憩終わり" instead, so the two never overlap."""
        seq = s.get("work_seq")
        if kind != "work" or not seq or seq == self.work_seq:
            return
        self.work_seq = seq
        by = s.get("work_started_by")
        work_sec = s.get("work_sec") or (s.get("config") or {}).get("work_min", 25) * 60
        elapsed = work_sec - (rem if rem is not None else work_sec)
        if by not in ("start", "reset") or elapsed > 10:  # the overlay was started late: no stale cue
            return
        text = (S_START if by == "start" else S_RESTART) % duration_text(work_sec)
        self._say(text)
        self.status(text, 6)

    def _prep_check(self, s):
        """案D (0.9.0): the timer looked at the screen before the prep and skipped it."""
        pc = s.get("prep_check")
        if pc and pc != self.prep_check and not pc.get("ok"):
            self._say(S_PREP_SKIPPED)
            self.status("トレーニングモードの対戦画面ではなかったので（判定：%s）、メニューの自動操作をやめました。"
                        "カジュアルマッチの検索は始まりません" % pc.get("screen"), 20)
        self.prep_check = pc

    def _pause(self, s):
        p = bool(s.get("paused"))
        if self.paused is not None and p != self.paused:
            self._say(S_PAUSE if p else S_RESUME)
            self.status("一時停止中" if p else "再開しました", 3600 if p else 6)
        self.paused = p

    def restrict_update(self, r):
        r = r or {}
        lv = r.get("level", 0) if r.get("active") else 0
        if lv and lv != self.r_level:
            labels = r.get("labels") or []
            label = labels[-1] if labels else ""
            self._say(S_HARDER % label)
            self.status("操作を難しくします（%s）：%s。%s" % (r.get("level_ja", ""), label, r.get("explain") or ""), 8)
        elif not lv and self.r_level:
            self._say(S_LIFTED)
            self.status(S_LIFTED, 5)
        susp = bool(lv and r.get("suspended") == "menu")
        if susp and not self.r_susp:
            self.status(S_MENU, 5)
        self.r_level, self.r_susp = lv, susp

    def phase_changed(self, old, new, s=None):
        s = s or {}
        online = bool(s.get("allow_online"))
        if new == "prep":
            self.status("対戦の準備をしています（メニューを自動で操作中）" if online
                        else "対戦の準備の時刻です（オンラインの操作はしない設定）", 8)
        if old in ("work", "prep") and new == "break":
            self._beep(1320, 600)
            self._say(S_BREAK)
            self.status("休憩です。カジュアルマッチの対戦相手を探しています" if online else "休憩です", 8)
        elif old == "break" and new in ("work", "prep") and s.get("work_started_by", "break") == "break":
            self._beep(660, 600)
            self._say(S_BACK_TO_WORK)
            self.status(S_BACK_TO_WORK, 8)


def fmt(rem):
    if rem is None:
        return "--:--"
    rem = int(rem + 0.999)
    return "%02d:%02d" % (rem // 60, rem % 60)


def make_click_through(root):
    """Give the Tk window the extended styles that keep it from taking focus or mouse input (Windows)."""
    if not IS_WIN:
        return
    import ctypes
    GWL_EXSTYLE = -20
    WS_EX_LAYERED, WS_EX_TRANSPARENT = 0x00080000, 0x00000020
    WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE, WS_EX_TOPMOST = 0x00000080, 0x08000000, 0x00000008
    root.update_idletasks()
    hwnd = ctypes.windll.user32.GetParent(root.winfo_id()) or root.winfo_id()
    user32 = ctypes.windll.user32
    style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
    user32.SetWindowLongW(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                          | WS_EX_NOACTIVATE | WS_EX_TOPMOST)
    # HWND_TOPMOST, no move/size, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE
    user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0010 | 0x0040 | 0x0002 | 0x0001)
    return hwnd


class FocusGuard(object):
    """0.8.2: if this window has become the foreground window, give the foreground back to SF6.

    On 2026-11-20 the 0.8.0 overlay (Tk is slower to map since the panels were rebuilt) appeared after
    start_pomodoro.ps1 had already brought SF6 to the front, took the foreground, and SF6 in the background ignored
    the pad: pws looked dead although MCPCon passed every press to the virtual pad. While this window is the
    foreground window, this process may hand the foreground to another window, so SetForegroundWindow works here.
    """

    def __init__(self):
        self.pid_cache = (None, 0.0)
        self.given = 0

    def _sf6_pid(self):
        pid, t = self.pid_cache
        if pid is None or time.time() - t > 10:
            from ensure_training import sf6_pid
            pid = sf6_pid()
            self.pid_cache = (pid, time.time())
        return pid

    def check(self):
        if not IS_WIN:
            return
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.windll.user32
        fg = user32.GetForegroundWindow()
        if not fg:
            return
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(fg, ctypes.byref(p))
        if p.value != os.getpid():
            return
        pid = self._sf6_pid()
        if not pid:
            return
        from ensure_training import bring_to_front
        bring_to_front(pid)
        self.given += 1
        print("overlay: this window had the foreground; gave it back to SF6 (pid %s, %d times)" % (pid, self.given),
              flush=True)


def rounded_rect(canvas, x0, y0, x1, y1, r, **kw):
    """A rounded rectangle as one smoothed polygon (Tk has no rounded rectangle)."""
    r = max(1, min(r, (x1 - x0) / 2.0, (y1 - y0) / 2.0))
    pts = [x0 + r, y0, x1 - r, y0, x1, y0, x1, y0 + r, x1, y1 - r, x1, y1, x1 - r, y1, x0 + r, y1,
           x0, y1, x0, y1 - r, x0, y0 + r, x0, y0]
    return canvas.create_polygon(pts, smooth=True, splinesteps=24, **kw)


def pick_font(families, wanted):
    low = {f.lower(): f for f in families}
    for w in wanted:
        if w.lower() in low:
            return low[w.lower()]
    return wanted[-1]


class Panels(object):
    """The drawing: a status row (temporary) above a main row (phase, time, restriction), each row on its own
    dark rounded panel. Only the canvas outside the panels has the key colour."""

    def __init__(self, root, canvas, w, h, scale, anchor):
        import tkinter.font as tkfont
        self.c, self.w, self.h, self.s, self.anchor = canvas, w, h, scale, anchor
        fams = tkfont.families(root)
        digits = pick_font(fams, ["Bahnschrift", "Segoe UI", "Helvetica Neue", "Helvetica"])
        words = pick_font(fams, ["Yu Gothic UI", "Meiryo UI", "Hiragino Sans", "Helvetica"])
        self.f_time = tkfont.Font(root=root, family=digits, size=-int(52 * scale), weight="bold")
        self.f_word = tkfont.Font(root=root, family=words, size=-int(20 * scale), weight="bold")
        self.f_status = tkfont.Font(root=root, family=words, size=-int(19 * scale), weight="bold")
        self.fonts = (digits, words)
        # the time slot is as wide as the widest time, so that the panel does not jitter as the digits change
        self.time_w = max(self.f_time.measure(t) for t in ("00:00", "88:88", "--:--"))
        self.items = []
        self.pad_x, self.pad_y, self.gap, self.r = int(16 * scale), int(6 * scale), int(14 * scale), int(14 * scale)
        # the digits have no descender, so the main row is a little lower than the font's line space
        self.row_h = self.f_time.metrics("linespace") + 2 * self.pad_y - int(10 * scale)
        self.status_line = self.f_status.metrics("linespace")

    def needed_height(self):
        """Main row + gap + a status panel of up to two lines."""
        return int(self.row_h + 8 * self.s + 2 * self.status_line + 2 * self.pad_y + 6)

    def draw(self, phase_text, phase_col, time_text, time_col, restrict_text, status_text):
        c, s = self.c, self.s
        for it in self.items:
            c.delete(it)
        self.items = []
        pad_x, pad_y, gap, r = self.pad_x, self.pad_y, self.gap, self.r
        # main row: [phase] [time] [restriction]
        parts = [(phase_text, self.f_word, phase_col, None), (time_text, self.f_time, time_col, self.time_w)]
        if restrict_text:
            parts.append((restrict_text, self.f_word, RESTRICT_COLOR, None))
        widths = [max(wd or 0, f.measure(t)) for t, f, col, wd in parts]
        row_w = min(sum(widths) + gap * (len(parts) - 1) + 2 * pad_x, self.w - 4)
        row_h = self.row_h
        x0 = self._x0(row_w)
        y1 = self.h - 3
        y0 = y1 - row_h
        self.items.append(rounded_rect(c, x0, y0, x0 + row_w, y1, r, fill=PANEL, outline=PANEL_EDGE))
        x = x0 + pad_x
        cy = (y0 + y1) / 2.0
        for (t, f, col, wd), width in zip(parts, widths):
            self.items.append(c.create_text(x + width / 2.0, cy, text=t, font=f, fill=col, anchor="center"))
            x += width + gap
        if status_text:
            # +8: Tk wraps a text whose width equals the wrap length exactly
            sw = min(self.f_status.measure(status_text) + 2 * pad_x + 8, self.w - 4)
            lines = 1 if self.f_status.measure(status_text) + 2 * pad_x + 8 <= self.w - 4 else 2
            sh = self.status_line * lines + 2 * pad_y
            sx0 = self._x0(sw)
            sy1 = y0 - int(8 * s)
            self.items.append(rounded_rect(c, sx0, sy1 - sh, sx0 + sw, sy1, r, fill=PANEL, outline=PANEL_EDGE))
            self.items.append(c.create_text(sx0 + sw / 2.0, sy1 - sh / 2.0, text=status_text, font=self.f_status,
                                            fill="#ffffff", anchor="center", justify="center",
                                            width=sw - 2 * pad_x + 4))

    def _x0(self, width):
        if self.anchor == "w":
            return 2
        if self.anchor == "e":
            return self.w - width - 2
        return (self.w - width) / 2.0


def main(argv=None):
    ap = argparse.ArgumentParser(description="Street Pomodoro Timer overlay %s" % VERSION)
    ap.add_argument("--state", default="http://127.0.0.1:8810/state")
    ap.add_argument("--corner", choices=["bottom-center", "top-left", "top-right", "bottom-left", "bottom-right"],
                    default="bottom-center",
                    help="bottom-center (default; Kurihara: the bottom-right covers the SF6 SA gauge)")
    ap.add_argument("--scale", type=float, default=0.8)
    ap.add_argument("--margin", type=int, default=8)
    ap.add_argument("--lift", type=int, default=None,
                    help="pixels to raise a bottom position (default 0 at bottom-center, 110 at the bottom corners "
                         "to keep the SF6 SA gauge visible at 1920x1080)")
    ap.add_argument("--rate", type=int, default=-2, help="System.Speech rate, -10 (slow) .. 10 (fast)")
    ap.add_argument("--no-voice", action="store_true", help="beeps only, no speech")
    ap.add_argument("--say-test", action="store_true", help="speak one sentence and beep, then exit")
    ap.add_argument("--preview-bg", default=None,
                    help="draw the transparent area in this colour (to look at the overlay where the transparent "
                         "colour key is not supported, e.g. on a Mac)")
    a = ap.parse_args(argv)
    RATE["value"] = a.rate
    if a.say_test:
        say(S_TEST, not a.no_voice)
        beep(880, 300)
        time.sleep(6)
        return

    import tkinter as tk
    root = tk.Tk()
    if not a.preview_bg:  # a preview is an ordinary window, so that a screenshot tool sees it
        root.overrideredirect(True)
    root.attributes("-topmost", True)
    bg = a.preview_bg or KEY
    if not a.preview_bg:
        try:
            root.attributes("-transparentcolor", KEY)
        except tk.TclError:
            pass
    root.configure(bg=bg)
    sw, sh = root.winfo_screenwidth(), root.winfo_screenheight()
    # the space between the two SA gauges: about 1000 px in the centre at 1920 wide (Kurihara)
    w = int(min(1000, sw * 0.52))
    probe = tk.Canvas(root)
    h = Panels(root, probe, w, 0, a.scale, "center").needed_height()  # the fonts decide the height
    probe.destroy()
    if a.corner == "bottom-center":
        lift = a.lift if a.lift is not None else 0
        x, anchor = (sw - w) // 2, "center"
    else:
        lift = a.lift if a.lift is not None else 110
        x = a.margin if "left" in a.corner else sw - w - a.margin
        anchor = "w" if "left" in a.corner else "e"
    y = a.margin if "top" in a.corner else sh - h - a.margin - lift
    root.geometry("%dx%d+%d+%d" % (w, h, x, y))
    canvas = tk.Canvas(root, width=w, height=h, bg=bg, highlightthickness=0, bd=0)
    canvas.pack(fill="both", expand=True)
    panels = Panels(root, canvas, w, h, a.scale, anchor)
    print("overlay %s fonts: digits=%s words=%s" % (VERSION, panels.fonts[0], panels.fonts[1]), flush=True)
    make_click_through(root)
    guard = FocusGuard()

    status = {"text": "", "until": 0.0}

    def show_status(text, sec=6):
        status["text"], status["until"] = text, time.time() + sec
    ann = Announcer(voice=not a.no_voice, status=show_status)
    shared = {"s": None, "err": None}

    def poll():
        while True:
            try:
                with urllib.request.urlopen(a.state, timeout=1) as r:
                    shared["s"], shared["err"] = json.loads(r.read().decode("utf-8")), None
            except Exception as e:
                shared["err"] = str(e)
            time.sleep(0.25)
    threading.Thread(target=poll, daemon=True).start()

    last = {"phase": None, "drawn": None}

    def tick():
        s = shared["s"]
        restrict_text = ""
        if s is None:
            phase_text, col, time_text = "タイマー未接続", "#cccccc", "--:--"
        else:
            phase = s.get("phase")
            if last["phase"] is not None and phase != last["phase"]:
                ann.phase_changed(last["phase"], phase, s)
            last["phase"] = phase
            ann.update(s)
            col = COLORS.get(phase, "#cccccc")
            rem = s.get("remaining_sec")
            if rem is not None and rem <= 10 and phase in ("work", "prep", "break"):
                col = RED
            # "作業中（1回目）": the phase and which Pomodoro (work + break) this is (Kurihara, 2026-11-19)
            phase_text = NAMES.get(phase, phase)
            if phase in ("work", "prep", "break") and s.get("cycle"):
                phase_text += "（%s回目）" % s.get("cycle")
            if s.get("paused"):
                phase_text += "・一時停止"
            time_text = "休憩中" if s.get("waiting_resume") else fmt(rem)
            r = s.get("restrict") or {}
            ann.restrict_update(r)
            if r.get("active") and r.get("level"):
                restrict_text = "制限 %s：%s%s" % (r.get("level_ja", ""), (r.get("labels") or [""])[-1],
                                                 "（メニュー中は解除）" if r.get("suspended") == "menu" else "")
        if status["until"] and time.time() > status["until"]:
            status["text"], status["until"] = "", 0.0
        drawn = (phase_text, col, time_text, restrict_text, status["text"])
        if drawn != last["drawn"]:  # redraw only when something changed
            panels.draw(phase_text, col, time_text, col, restrict_text, status["text"])
            last["drawn"] = drawn
        root.attributes("-topmost", True)  # stay above SF6 even if it re-asserts its z-order
        try:
            guard.check()  # never keep the foreground: SF6 ignores the pad when it is behind
        except Exception as e:
            print("overlay: focus guard failed: %s" % e, flush=True)
        root.after(250, tick)
    tick()
    root.mainloop()


if __name__ == "__main__":
    main()
