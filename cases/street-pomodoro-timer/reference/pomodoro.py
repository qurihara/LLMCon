# -*- coding: utf-8 -*-
"""Street Pomodoro Timer: a Pomodoro timer (25 min work, 5 min Street Fighter 6 break) that drives the game
through LLMCon (MCPCon) so that Casual Match matchmaking starts exactly when the work period ends.

Each cycle:
  work    : the timer counts the work period (default 25 min).
  prep    : some seconds before the end of the work period, the program walks the SF6 menu up to the
            screen just before "start matchmaking" (route file, "prep" steps). The start time of the prep is
            computed backwards from the sum of the step waits plus a margin, so that the prep is done
            before the work period ends.
  start   : exactly at the end of the work period, the program sends the last step ("start" in the route
            file). This step is sent ONLY with --allow-online, because Casual Match is an online match
            against other people. Without the flag the program stops just before matchmaking.
  break   : the break (default 5 min). When it ends, the next work period starts (auto), or the program
            waits for POST /resume (--break-end manual).

Buttons go to MCPCon as POST /connect/apply {"kind": "inject_tap", ...}. Every press is at least 3 frames
(the game reads each step with a jitter of +-1..2 frames; 2-frame steps were lost in hadoken-slides).

Control over HTTP (the page at / has buttons for all of these; an LLM can call them with curl):
  GET  /state                     the state as JSON (phase, remaining_sec, cycle, paused, config, restrict, ...)
  POST /pause                     freeze the timer (prep taps, the start tap and the restrictions wait too)
  POST /resume                    leave the pause; when not paused, end a --break-end manual break
  POST /reset   {"scope": "cycle"|"all"}   redo this cycle from the start of its work period, or go back to cycle 1
  POST /stop                      stop the program
  POST /config  {"work": 25, "break": 5, "allow_online": false, "restrict": true, "volume": true,
                 "volume_work": 30}            each setting takes effect from the next phase that uses it
Every request may carry "from" (who asked); it is written into the log with the action. Without "from" the
log has the client's address. --host 127.0.0.1 (default) only answers on this machine; on the ROG Ally use
--host 127.0.0.1,<TAILSCALE_ADDRESS> (this machine and Tailscale) to be reached from another machine. There is no password.

  python3 pomodoro.py --mcpcon http://<TAILSCALE_ADDRESS>:8777/ --route route.sf6_training.json
  python3 pomodoro.py --dry-run --work 1 --break 0.5        # send nothing, short periods
  --speed 60   run the clock 60 times faster (simulation only)
SF6's own volume is lowered during work (volume.py, Windows with pycaw only; --volume-work, --no-volume).
No LLM is on this path: the program calls MCPCon's vocabulary directly.
"""
import argparse
import json
import os
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from version import VERSION  # noqa: E402

SOURCE = "pomodoro-timer"
MIN_FRAMES = 3


class Clock(object):
    """Seconds since start. speed > 1 runs faster than real time (for the simulation).

    pause() freezes the clock and resume() lets it go on from the same value, so every time computed from the
    clock (the end of work, the prep, the start tap, the end of the break) moves later by the paused time,
    at any speed."""

    def __init__(self, speed=1.0):
        self.speed = float(speed)
        self.t0 = time.monotonic()
        self.paused_at = None   # monotonic time when paused, or None
        self.paused_total = 0.0
        self._lock = threading.Lock()

    @property
    def paused(self):
        return self.paused_at is not None

    def now(self):
        with self._lock:
            m = self.paused_at if self.paused_at is not None else time.monotonic()
            return (m - self.t0 - self.paused_total) * self.speed

    def pause(self):
        with self._lock:
            if self.paused_at is None:
                self.paused_at = time.monotonic()
                return True
            return False

    def resume(self):
        with self._lock:
            if self.paused_at is None:
                return False
            self.paused_total += time.monotonic() - self.paused_at
            self.paused_at = None
            return True

    def sleep_until(self, t, stop=None):
        """Sleep until clock time t. Returns False if stop (an Event) was set first."""
        while True:
            if stop is not None and stop.is_set():
                return False
            if self.paused:
                time.sleep(0.02)
                continue
            left = (t - self.now()) / self.speed
            if left <= 0:
                return True
            # coarse sleep while far away, fine sleep near the target for an exact start
            time.sleep(min(left, 0.2) if left > 0.05 else max(left - 0.001, 0) or 0.0005)


def post_json(url, payload, timeout=1.0):
    req = urllib.request.Request(url, data=json.dumps(payload).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def load_route(path):
    with open(path, encoding="utf-8") as f:
        route = json.load(f)
    for st in route.get("prep", []) + [route["start"]]:
        if int(st.get("frames", MIN_FRAMES)) < MIN_FRAMES:
            raise ValueError("step '%s' has frames < %d" % (st.get("label"), MIN_FRAMES))
    return route


def prep_seconds(route):
    return sum(float(st.get("wait", 0)) for st in route.get("prep", [])) + float(route.get("margin_sec", 0))


class Pomodoro(object):
    def __init__(self, route, mcpcon, clock, work_sec, break_sec, allow_online=False, break_end="auto",
                 cycles=0, dry_run=False, log_path=None, say=print, restrictor_factory=None,
                 restrict_enabled=None, volume=None, volume_work=0.3, volume_break=1.0, screen_check=None):
        self.route, self.clock = route, clock
        self.mcpcon = mcpcon.rstrip("/") if mcpcon else None
        self.work_sec, self.break_sec = float(work_sec), float(break_sec)
        self.allow_online, self.break_end = allow_online, break_end
        self.cycles, self.dry_run, self.say = cycles, dry_run, say
        self.stop = threading.Event()
        self.resume = threading.Event()   # ends a --break-end manual break
        self.wake = threading.Event()     # set by stop and reset: every wait of the cycle returns at once
        self.reset_scope = None           # "cycle" or "all", while a reset is pending
        self.lock = threading.Lock()
        self.state = {"version": VERSION, "phase": "idle", "cycle": 0, "phase_ends_at": None,
                      "allow_online": allow_online, "break_end": break_end, "dry_run": dry_run,
                      "last_event": None, "last_control": None, "work_seq": 0, "work_started_by": None}
        self.log = open(log_path, "a", encoding="utf-8") if log_path else None
        # called at the end of each break with the event function; returns a started restrict.Restrictor
        self.restrictor_factory = restrictor_factory
        self.restrict_enabled = bool(restrictor_factory) if restrict_enabled is None else bool(restrict_enabled)
        self.restrictor = None
        self.volume = volume              # volume.AppVolume or None
        self.volume_work, self.volume_break = float(volume_work), float(volume_break)
        self._next_work_by = "start"
        # 0.9.0 (案D, Kurihara 2026-11-20): screen_check() -> the name of the SF6 screen ("training", "title", ...,
        # see ensure_training.classify). Called just before the prep taps; anything but "training" skips the prep
        # and the start tap, so that the menu taps never land on another screen (Kurihara forgot to go back).
        self.screen_check = screen_check
        self.state["prep_check"] = None

    # -- logging and state --------------------------------------------------------------------------
    def event(self, kind, **kw):
        rec = dict(t=round(self.clock.now(), 3), wall=time.strftime("%H:%M:%S"), kind=kind,
                   cycle=self.state["cycle"], version=VERSION, **kw)
        with self.lock:
            self.state["last_event"] = rec
        if self.log:
            self.log.write(json.dumps(rec, ensure_ascii=False) + "\n")
            self.log.flush()
        self.say("[%s t=%8.2f] %s %s" % (rec["wall"], rec["t"], kind,
                                         json.dumps(kw, ensure_ascii=False) if kw else ""))
        return rec

    def set_phase(self, phase, ends_at):
        with self.lock:
            self.state["phase"], self.state["phase_ends_at"] = phase, ends_at
            if phase == "work":
                self.state["prep_check"] = None
                self.state["work_seq"] += 1
                self.state["work_started_by"] = self._next_work_by
                self._next_work_by = "break"
        self.event("phase", phase=phase, ends_at=None if ends_at is None else round(ends_at, 3))
        if self.volume is not None:
            if phase in ("work", "prep"):
                self.volume.set(self.volume_work)
            elif phase == "break":
                self.volume.set(self.volume_break)

    def config(self):
        return {"work_min": round(self.work_sec / 60.0, 3), "break_min": round(self.break_sec / 60.0, 3),
                "allow_online": self.allow_online, "restrict": self.restrict_enabled,
                "restrict_available": bool(self.restrictor_factory),
                "volume": bool(self.volume and self.volume.enabled),
                "volume_available": bool(self.volume and self.volume.available),
                "volume_work": int(round(self.volume_work * 100)), "break_end": self.break_end}

    def snapshot(self):
        with self.lock:
            s = dict(self.state)
        s["now"] = round(self.clock.now(), 3)
        s["paused"] = self.clock.paused
        s["allow_online"] = self.allow_online
        s["work_sec"], s["break_sec"] = self.work_sec, self.break_sec
        s["config"] = self.config()
        s["restrict"] = self.restrictor.snapshot() if self.restrictor else None
        s["volume"] = self.volume.snapshot() if self.volume else None
        s["waiting_resume"] = s["phase"] == "break" and s["phase_ends_at"] is None
        s["remaining_sec"] = None if s["phase_ends_at"] is None else max(0.0, round(s["phase_ends_at"] - s["now"], 1))
        return s

    # -- control (HTTP and tests) -------------------------------------------------------------------
    def control(self, action, by=None, **kw):
        rec = self.event("control", action=action, by=by, **kw)
        with self.lock:
            self.state["last_control"] = {"action": action, "by": by, "wall": rec["wall"]}
        return rec

    def do_pause(self, by=None):
        changed = self.clock.pause()
        if changed and self.restrictor:
            self.restrictor.pause()
        self.control("pause", by, changed=changed, phase=self.state["phase"])
        return changed

    def do_resume(self, by=None):
        """Leave a pause; when not paused, end a --break-end manual break (the old meaning of /resume)."""
        if self.clock.paused:
            self.clock.resume()
            if self.restrictor:
                self.restrictor.resume()
            self.control("resume", by, what="pause")
            return "pause"
        self.resume.set()
        self.control("resume", by, what="break")
        return "break"

    def do_reset(self, scope="cycle", by=None):
        if scope not in ("cycle", "all"):
            raise ValueError("scope must be 'cycle' or 'all'")
        phase = self.state["phase"]
        self.control("reset", by, scope=scope, phase=phase)
        if phase == "prep" and self.allow_online:
            # the prep taps may have left the Training Mode menu open with Casual Match ON (paused); nothing is
            # pressed now, because B would start online matchmaking
            self.event("reset_during_prep", note="the SF6 menu may be open with Casual Match ON; close it by hand")
        self.reset_scope = scope
        self.wake.set()

    def do_stop(self, by=None):
        self.control("stop", by)
        self.stop.set()
        self.wake.set()

    def do_config(self, cfg, by=None):
        """Change the settings; each one takes effect from the next phase that uses it:
        work (min) at the next work period, break (min) at the next break, allow_online at the next prep,
        restrict at the next end of a break (turning it off also ends restrictions running now),
        volume / volume_work (percent) at the next phase change (turning it off restores the volume now)."""
        done = {}
        with self.lock:
            if "work" in cfg:
                v = float(cfg["work"])
                if not 0.05 <= v <= 240:
                    raise ValueError("work must be 0.05 .. 240 minutes")
                self.work_sec = v * 60
                done["work"] = v
            if "break" in cfg:
                v = float(cfg["break"])
                if not 0.05 <= v <= 120:
                    raise ValueError("break must be 0.05 .. 120 minutes")
                self.break_sec = v * 60
                done["break"] = v
            if "allow_online" in cfg:
                self.allow_online = bool(cfg["allow_online"])
                done["allow_online"] = self.allow_online
            if "restrict" in cfg:
                on = bool(cfg["restrict"])
                if on and not self.restrictor_factory:
                    raise ValueError("restrict.py is not available here")
                self.restrict_enabled = on
                done["restrict"] = on
            if "volume_work" in cfg:
                v = float(cfg["volume_work"])
                if not 0 <= v <= 100:
                    raise ValueError("volume_work must be 0 .. 100 percent")
                self.volume_work = v / 100.0
                done["volume_work"] = v
        if done.get("restrict") is False and self.restrictor:
            self.restrictor.stop("turned off")
        if "volume" in cfg and self.volume is not None:
            self.volume.set_enabled(bool(cfg["volume"]))
            done["volume"] = self.volume.enabled
        self.control("config", by, **done)
        return done

    # -- buttons ------------------------------------------------------------------------------------
    def tap(self, step, role):
        frames = max(MIN_FRAMES, int(step.get("frames", 4)))
        payload = {"kind": "inject_tap", "buttons": step["buttons"], "frames": frames, "from": SOURCE}
        sent = False
        err = None
        if not self.dry_run and self.mcpcon:
            try:
                post_json(self.mcpcon + "/connect/apply", payload)
                sent = True
            except Exception as e:  # keep the timer running even if MCPCon is down
                err = str(e)
        return self.event("tap", role=role, label=step.get("label"), buttons=step["buttons"], frames=frames,
                          sent=sent, error=err)

    def check_screen(self, tries=4, gap=0.4):
        """The SF6 screen just before the prep: "training" when two grabs in a row say so, otherwise the last name
        seen ("error" when the screen could not be taken). A frame in the middle of an effect may be misjudged, so a
        single odd frame does not skip the prep."""
        last, prev = "error", None
        for _ in range(tries):
            try:
                last = self.screen_check() or "unknown"
            except Exception as e:
                self.event("screen_check_failed", error=str(e))
                last = "error"
            if last == "training" and prev == "training":
                return "training"
            prev = last
            time.sleep(gap)
        return "unconfirmed" if last == "training" else last  # a lone Training frame at the end is not enough

    # -- one cycle ----------------------------------------------------------------------------------
    def run_cycle(self, t0):
        """One work period and one break. Returns the clock time at which the next cycle starts, or None when
        stopped or reset (self.wake). A pause freezes the clock, so every wait below simply takes longer."""
        work_end = t0 + self.work_sec
        # the screen check (案D) takes up to about 2 s, so it starts that much earlier than the prep taps would
        check_sec = 3.0 if (self.screen_check is not None and self.allow_online) else 0.0
        prep_at = max(t0, work_end - prep_seconds(self.route) - check_sec)
        self.set_phase("work", work_end)
        if not self.clock.sleep_until(prep_at, self.wake):
            return None
        self.set_phase("prep", work_end)
        online = self.allow_online  # fixed for this prep and its start tap (a change applies from the next prep)
        if not online:
            # The prep steps arm Casual Match (ON, paused while the menu is open): one press of B by anyone
            # would then start online matchmaking. Without --allow-online nothing is sent at all.
            self.event("prep_held", reason="--allow-online is not given; the prep steps would arm Casual Match")
        skipped = False
        if online and self.screen_check is not None:
            screen = self.check_screen()
            ok = screen == "training"
            with self.lock:
                self.state["prep_check"] = {"ok": ok, "screen": screen, "wall": time.strftime("%H:%M:%S")}
            if ok:
                self.event("prep_screen_ok", screen=screen)
            else:
                skipped = True
                self.event("prep_skipped", screen=screen,
                           reason="SF6 is not on the Training Mode fight screen; no menu tap is sent this time")
        for st in self.route.get("prep", []):
            if self.wake.is_set():
                return None
            if online and not skipped:
                # a pause that came in just now holds the tap too (sleep_until waits while the clock is paused)
                if not self.clock.sleep_until(self.clock.now(), self.wake):
                    return None
                self.tap(st, "prep")
            if not self.clock.sleep_until(self.clock.now() + float(st.get("wait", 0)), self.wake):
                return None
        late_prep = self.clock.now() > work_end
        if late_prep:
            self.event("prep_late", over_sec=round(self.clock.now() - work_end, 3))
        if not self.clock.sleep_until(work_end, self.wake):
            return None
        if online and not skipped:
            rec = self.tap(self.route["start"], "start")
            self.event("start_offset", offset_sec=round(rec["t"] - work_end, 4))
        elif skipped:
            self.event("start_held", reason="the screen was not Training Mode before the prep",
                       label=self.route["start"].get("label"))
        else:
            self.event("start_held", reason="--allow-online is not given; stopped just before matchmaking",
                       label=self.route["start"].get("label"))
        break_end = work_end + self.break_sec  # from the scheduled end of work, so the cycles do not drift
        if self.break_end == "auto":
            self.set_phase("break", break_end)
            if not self.clock.sleep_until(break_end, self.wake):
                return None
        else:
            self.resume.clear()
            self.set_phase("break", None)
            while not self.resume.is_set():
                if self.wake.wait(0.05):
                    return None
        self.event("break_end")
        if self.restrictor:
            self.restrictor.stop("next break")
            self.restrictor = None
        if self.restrictor_factory and self.restrict_enabled:
            self.restrictor = self.restrictor_factory(lambda kind, **kw: self.event(kind, **kw))
        return break_end if self.break_end == "auto" else self.clock.now()

    def run(self):
        self.event("start", work_sec=self.work_sec, break_sec=self.break_sec, prep_sec=prep_seconds(self.route),
                   allow_online=self.allow_online, mcpcon=self.mcpcon, dry_run=self.dry_run,
                   restrict=self.restrict_enabled, volume=self.volume.snapshot() if self.volume else None)
        t = self.clock.now()
        n = 0
        while not self.stop.is_set():
            n += 1
            with self.lock:
                self.state["cycle"] = n
            t = self.run_cycle(t)
            if t is None and not self.stop.is_set() and self.reset_scope:
                scope, self.reset_scope = self.reset_scope, None
                self.wake.clear()
                if self.clock.paused:
                    self.clock.resume()
                if self.restrictor:
                    self.restrictor.stop("reset")
                    self.restrictor = None
                n = 0 if scope == "all" else n - 1
                self._next_work_by = "reset"
                t = self.clock.now()
                continue
            if t is None or (self.cycles and n >= self.cycles):
                break
        if self.restrictor:
            self.restrictor.stop("timer stopped")
        if self.volume is not None:
            self.volume.restore()
        self.set_phase("stopped", None)


PAGE_FILE = os.path.join(HERE, "control.html")


class ThreadingHTTP(ThreadingMixIn, HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def serve(app, host, port):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _send(self, code, body, ctype="application/json; charset=utf-8"):
            data = body.encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

        def _json(self, code, obj):
            return self._send(code, json.dumps(obj, ensure_ascii=False))

        def _args(self):
            """The JSON body merged with the query string (?scope=all&from=claude) for easy curl calls."""
            from urllib.parse import urlsplit, parse_qsl
            args = dict(parse_qsl(urlsplit(self.path).query))
            n = int(self.headers.get("Content-Length") or 0)
            if n:
                raw = self.rfile.read(n)
                try:
                    body = json.loads(raw.decode("utf-8") or "{}")
                    if isinstance(body, dict):
                        args.update(body)
                except ValueError:
                    pass
            return args

        def _by(self, args):
            return str(args.get("from") or "http %s" % self.client_address[0])[:80]

        def do_GET(self):
            path = self.path.split("?")[0]
            if path == "/state":
                return self._json(200, app.snapshot())
            if path in ("/", "/index.html"):
                try:
                    with open(PAGE_FILE, encoding="utf-8") as f:
                        return self._send(200, f.read(), "text/html; charset=utf-8")
                except OSError:
                    return self._send(500, "control.html is missing", "text/plain; charset=utf-8")
            return self._json(404, {"ok": False, "error": "not found"})

        def do_POST(self):
            path = self.path.split("?")[0]
            args = self._args()
            by = self._by(args)
            try:
                if path == "/pause":
                    out = {"changed": app.do_pause(by)}
                elif path == "/resume":
                    out = {"resumed": app.do_resume(by)}
                elif path == "/reset":
                    app.do_reset(args.get("scope", "cycle"), by)
                    out = {"scope": args.get("scope", "cycle")}
                elif path == "/stop":
                    app.do_stop(by)
                    out = {}
                elif path == "/config":
                    cfg = {k: v for k, v in args.items() if k != "from"}
                    for k in ("allow_online", "restrict", "volume"):  # "false" from a query string
                        if isinstance(cfg.get(k), str):
                            cfg[k] = cfg[k].lower() in ("1", "true", "on", "yes")
                    out = {"applied": app.do_config(cfg, by)}
                else:
                    return self._json(404, {"ok": False, "error": "not found"})
            except (ValueError, TypeError) as e:
                return self._json(400, {"ok": False, "error": str(e)})
            out["ok"] = True
            out["state"] = app.snapshot()
            return self._json(200, out)

    srv = ThreadingHTTP((host, port), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


def main(argv=None):
    ap = argparse.ArgumentParser(description="Street Pomodoro Timer %s" % VERSION)
    ap.add_argument("--mcpcon", default="http://127.0.0.1:8777/")
    ap.add_argument("--route", default=os.path.join(HERE, "route.sf6_training.json"))
    ap.add_argument("--work", type=float, default=25.0, help="work period in minutes")
    ap.add_argument("--break", dest="brk", type=float, default=5.0, help="break in minutes")
    ap.add_argument("--cycles", type=int, default=0, help="0 = repeat until stopped")
    ap.add_argument("--allow-online", action="store_true",
                    help="send the last step that starts Casual Match matchmaking (online, against other people)")
    ap.add_argument("--break-end", choices=["auto", "manual"], default="auto")
    ap.add_argument("--dry-run", action="store_true", help="send nothing to MCPCon")
    ap.add_argument("--restrict", action="store_true",
                    help="after each break, make the game harder step by step (Finalize Fantasy; restrict.py)")
    ap.add_argument("--restrict-plan", default=None, help="Finalize Fantasy plan (default: SF6 plan)")
    ap.add_argument("--restrict-picks", default=None, help="one option label per level, comma separated")
    ap.add_argument("--restrict-step", type=float, default=30.0, help="seconds per level")
    ap.add_argument("--restrict-idle", type=float, default=15.0, help="seconds without input that end it")
    ap.add_argument("--restrict-max", type=float, default=600.0, help="seconds after which it ends anyway")
    ap.add_argument("--volume-work", type=float, default=30.0,
                    help="SF6's own volume (percent) during work and prep; 100 during the break (Windows, pycaw)")
    ap.add_argument("--volume-break", type=float, default=100.0, help="SF6's own volume (percent) during the break")
    ap.add_argument("--no-volume", action="store_true", help="do not touch SF6's volume")
    ap.add_argument("--speed", type=float, default=1.0, help="clock speed (simulation only)")
    ap.add_argument("--screen-check", choices=["auto", "off"], default="auto",
                    help="auto: on Windows, look at the SF6 screen before the prep and skip it unless it is the "
                         "Training Mode fight screen (ensure_training.classify)")
    ap.add_argument("--screen-fake", default=None, help="simulation only: the screen name the check returns")
    ap.add_argument("--host", default="127.0.0.1",
                    help="address(es) to listen on, comma separated; 127.0.0.1,<TAILSCALE_ADDRESS> (this machine and "
                         "Tailscale) or 0.0.0.0 to be reached from other machines (there is no password: never on a "
                         "public network)")
    ap.add_argument("--port", type=int, default=8810)
    ap.add_argument("--log", default=None)
    a = ap.parse_args(argv)
    log = a.log or os.path.join(HERE, "logs", time.strftime("run-%Y%m%d-%H%M%S.jsonl"))
    os.makedirs(os.path.dirname(log), exist_ok=True)
    factory = None
    try:
        import restrict
        steps = restrict.load_steps(a.restrict_plan or restrict.DEFAULT_PLAN,
                                    a.restrict_picks.split(",") if a.restrict_picks else restrict.DEFAULT_PICKS)

        def factory(event):
            r = restrict.Restrictor(a.mcpcon, steps, step_sec=a.restrict_step, idle_sec=a.restrict_idle,
                                    max_sec=a.restrict_max, dry_run=a.dry_run, event=event)
            r.start()
            return r
    except Exception as e:  # restrict.py needs ../sf6/pilot.py and the finalize-fantasy plan
        if a.restrict:
            raise
        print("restrict.py is not available (%s); POST /config cannot turn it on" % e, flush=True)
    import volume
    holder = {}
    vol = volume.AppVolume(enabled=not a.no_volume,
                           event=lambda kind, **kw: holder["app"].event(kind, **kw) if "app" in holder
                           else print(kind, kw, flush=True))
    app = Pomodoro(load_route(a.route), a.mcpcon, Clock(a.speed), a.work * 60, a.brk * 60,
                   allow_online=a.allow_online, break_end=a.break_end, cycles=a.cycles, dry_run=a.dry_run,
                   log_path=log, restrictor_factory=factory, restrict_enabled=a.restrict, volume=vol,
                   volume_work=a.volume_work / 100.0, volume_break=a.volume_break / 100.0)
    holder["app"] = app
    if a.screen_fake:
        app.screen_check = lambda: a.screen_fake
    elif a.screen_check == "auto" and sys.platform.startswith("win"):
        try:
            import ensure_training
            grabber = ensure_training.Grabber()
            app.screen_check = lambda: ensure_training.classify(grabber.grab())[0]
        except Exception as e:
            app.event("screen_check_unavailable", error=str(e))
    if vol.reason and not a.no_volume:
        app.event("volume_unavailable", reason=vol.reason)
    bound = 0
    for host in [h.strip() for h in a.host.split(",") if h.strip()]:
        try:
            serve(app, host, a.port)
            bound += 1
            print("Street Pomodoro Timer %s  control: http://%s:%d/" % (VERSION, host, a.port), flush=True)
        except OSError as e:  # e.g. Tailscale is not up yet: keep the timer running on the other addresses
            app.event("listen_failed", host=host, port=a.port, error=str(e))
    if not bound:
        raise SystemExit("could not listen on %s:%d" % (a.host, a.port))
    print("log: %s" % log, flush=True)
    try:
        app.run()
    except KeyboardInterrupt:
        app.do_stop("keyboard")
        if vol is not None:
            vol.restore()


if __name__ == "__main__":
    main()
