# -*- coding: utf-8 -*-
"""Simulation tests for pomodoro.py with a mock MCPCon (no game, no ROG Ally).

The mock accepts POST /connect/apply and records each inject_tap with the clock time at which it arrived.
Cases:
  A  fast clock (x300), 25/5 min, 2 cycles, --allow-online: prep is done before the end of work, the start
     tap arrives at the end of work, and the second cycle starts when the break ends.
  B  real clock, 0.4/0.1 min, 1 cycle, --allow-online: how exactly the start tap arrives (real seconds).
  C  without --allow-online: nothing is sent (the prep would arm Casual Match), prep and start are held.
  D  --break-end manual: the next work period waits for POST /resume.
  E  MCPCon is down: the timer keeps going and records the error.
  G  POST /pause and /resume (x300): during a pause the clock, the remaining time and the taps stand still, the
     restrictor is paused too, and after the pause the start tap still comes at the (moved) end of work.
  H  POST /reset: scope "cycle" redoes the work period of the same cycle; scope "all" goes back to cycle 1.
  I  POST /config: work, break and allow_online take effect from the next phase that uses them; a bad value
     is refused (400); GET / serves the control page; every control is logged with "from".
  python3 test/run_sim.py
"""
import json
import os
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import pomodoro  # noqa: E402

ROUTE = pomodoro.load_route(os.path.join(os.path.dirname(HERE), "route.sf6_training.json"))
N_PREP = len(ROUTE["prep"])
FAILS = []


def check(cond, msg):
    print(("  ok    " if cond else "  FAIL  ") + msg)
    if not cond:
        FAILS.append(msg)


class Mock(object):
    def __init__(self, port, clock_ref):
        self.taps, self.clock_ref = [], clock_ref
        mock = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_POST(self):
                n = int(self.headers.get("Content-Length") or 0)
                body = json.loads(self.rfile.read(n) or b"{}")
                if self.path.startswith("/connect/apply") and body.get("kind") == "inject_tap":
                    mock.taps.append(dict(body, at=mock.clock_ref[0].now()))
                self.send_response(200)
                self.send_header("Content-Length", "2")
                self.end_headers()
                self.wfile.write(b"{}")

        self.srv = HTTPServer(("127.0.0.1", port), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def close(self):
        self.srv.shutdown()
        self.srv.server_close()


def run(speed, work_min, break_min, cycles, allow_online, break_end="auto", mcpcon_up=True, port=18777,
        during=None, **kw):
    clock = pomodoro.Clock(speed)
    ref = [clock]
    mock = Mock(port, ref) if mcpcon_up else None
    app = pomodoro.Pomodoro(ROUTE, "http://127.0.0.1:%d/" % port, clock, work_min * 60, break_min * 60,
                            allow_online=allow_online, break_end=break_end, cycles=cycles,
                            say=lambda *a: None, **kw)
    events = []
    orig = app.event

    def rec(kind, **kw):
        r = orig(kind, **kw)
        events.append(r)
        return r
    app.event = rec
    th = threading.Thread(target=app.run)
    th.start()
    if during:
        during(app, clock)
    th.join(timeout=120)
    if mock:
        mock.close()
    return events, (mock.taps if mock else []), app


def phases(events, name):
    return [e for e in events if e["kind"] == "phase" and e["phase"] == name]


def case_a():
    print("A  x300, 25/5 min, 2 cycles, --allow-online")
    sp = 300.0
    ev, taps, _ = run(sp, 25, 5, 2, True)
    work = phases(ev, "work")
    check(len(work) == 2, "2 work periods")
    check(len(taps) == 2 * (N_PREP + 1), "%d taps arrived (%d prep + 1 start per cycle)" % (len(taps), N_PREP))
    check(all(t["frames"] >= 3 for t in taps), "every tap is 3 frames or longer")
    check(not [e for e in ev if e["kind"] == "prep_late"], "prep was done before the end of work")
    for c in (1, 2):
        w_end = [e for e in work if e["cycle"] == c][0]["ends_at"]
        prep = [e for e in ev if e["kind"] == "tap" and e["role"] == "prep" and e["cycle"] == c]
        start = [e for e in ev if e["kind"] == "tap" and e["role"] == "start" and e["cycle"] == c]
        # the event time is taken after the HTTP post (a few real ms, x300 on this clock), so judge in real s
        lead = w_end - prep[0]["t"]
        lag = (pomodoro.prep_seconds(ROUTE) - lead) / sp
        check(0 <= lag < 0.03, "cycle %d: prep began %.1f clock s before the end of work (expected %.1f; %.4f real s late)"
              % (c, lead, pomodoro.prep_seconds(ROUTE), lag))
        off = (start[0]["t"] - w_end) / sp
        check(0 <= off < 0.02, "cycle %d: start tap sent %.4f real s after the end of work" % (c, off))
    w1, w2 = work[0]["ends_at"], work[1]["ends_at"]
    gap = w2 - w1
    check(abs(gap - 30 * 60) < 2.0, "second work period ended %.1f min after the first (expected 30.0)" % (gap / 60))
    check(ev[-1]["kind"] == "phase" and ev[-1]["phase"] == "stopped", "stopped after 2 cycles")



def case_j():
    print("J  案D: the screen is checked before the prep (x300, 25/5 min, --allow-online)")
    ev, taps, app = run(300.0, 25, 5, 1, True, port=18790, screen_check=lambda: "title")
    check(len(taps) == 0, "screen 'title': no tap arrived, neither prep nor start (%d)" % len(taps))
    sk = [e for e in ev if e["kind"] == "prep_skipped"]
    check(len(sk) == 1 and sk[0]["screen"] == "title", "prep_skipped recorded with the screen name")
    check(len([e for e in ev if e["kind"] == "start_held"]) == 1, "start_held recorded")
    check(len(phases(ev, "break")) == 1, "the break still came (the timer goes on)")
    ev, taps, app = run(300.0, 25, 5, 1, True, port=18791, screen_check=lambda: "training")
    check(len(taps) == N_PREP + 1, "screen 'training': %d taps arrived (all prep + start)" % len(taps))
    check(len([e for e in ev if e["kind"] == "prep_screen_ok"]) == 1, "prep_screen_ok recorded")
    seq = iter(["training", "battle_setting", "training", "unknown"])
    ev, taps, app = run(300.0, 25, 5, 1, True, port=18792, screen_check=lambda: next(seq, "unknown"))
    check(len(taps) == 0, "no two Training frames in a row: skipped (%d taps)" % len(taps))
    def boom():
        raise RuntimeError("no screen")
    ev, taps, app = run(300.0, 25, 5, 1, True, port=18793, screen_check=boom)
    check(len(taps) == 0 and [e for e in ev if e["kind"] == "screen_check_failed"],
          "a screen that cannot be taken: skipped and recorded")
    ev, taps, app = run(300.0, 25, 5, 1, False, port=18794, screen_check=lambda: "title")
    check(not [e for e in ev if e["kind"] in ("prep_skipped", "prep_screen_ok")],
          "without --allow-online the screen is not checked (nothing is sent anyway)")


def case_b():
    print("B  real clock, 0.4/0.1 min, 1 cycle, --allow-online (takes about 30 s)")
    ev, taps, _ = run(1.0, 0.4, 0.1, 1, True, port=18778)
    w_end = phases(ev, "work")[0]["ends_at"]
    st = [t for t in taps if t["buttons"] == ROUTE["start"]["buttons"] and t["at"] >= w_end - 0.5]
    check(len(st) == 1, "one start tap arrived at the mock MCPCon")
    if st:
        off = st[0]["at"] - w_end
        check(0 <= off < 0.05, "the start tap arrived %.4f s after the end of work" % off)


def case_c():
    print("C  without --allow-online")
    ev, taps, _ = run(300.0, 25, 5, 1, False, port=18779)
    check(len(taps) == 0, "no tap arrived, not even the prep taps that would arm Casual Match (%d)" % len(taps))
    check(len([e for e in ev if e["kind"] == "prep_held"]) == 1, "prep_held recorded")
    check(len([e for e in ev if e["kind"] == "start_held"]) == 1, "start_held recorded")


def case_d():
    print("D  --break-end manual")
    seen = []

    def during(app, clock):
        # In each of the 2 breaks: wait 1 real second (300 clock s, longer than the 5 min break), check that
        # the timer is still in the break with no end time, then send POST /resume through the HTTP server.
        srv = pomodoro.serve(app, "127.0.0.1", 18810)
        try:
            for _ in range(2):
                while app.snapshot()["phase"] != "break":
                    time.sleep(0.01)
                time.sleep(1.0)
                s = app.snapshot()
                seen.append((s["phase"], s["remaining_sec"]))
                url = "http://127.0.0.1:18810/resume"
                urllib.request.urlopen(urllib.request.Request(url, data=b"", method="POST"), timeout=2).read()
                while app.snapshot()["phase"] == "break":
                    time.sleep(0.01)
        finally:
            srv.shutdown()
            srv.server_close()
    ev, taps, app = run(300.0, 25, 5, 2, True, break_end="manual", port=18780, during=during)
    check(seen == [("break", None), ("break", None)],
          "both breaks lasted past 5 min with no end time until POST /resume")
    check(len(phases(ev, "work")) == 2, "the second work period started after POST /resume")
    check(ev[-1]["phase"] == "stopped", "stopped after 2 cycles")


def case_e():
    print("E  MCPCon is down")
    ev, taps, _ = run(300.0, 25, 5, 1, True, mcpcon_up=False, port=18781)
    tap_ev = [e for e in ev if e["kind"] == "tap"]
    check(len(tap_ev) == N_PREP + 1 and all(e["error"] for e in tap_ev), "all taps recorded with an error")
    check(ev[-1]["phase"] == "stopped", "the timer ran to the end")


def http(port, method, path, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request("http://127.0.0.1:%d%s" % (port, path), data=data if method == "POST" else None,
                                 method=method, headers={"Content-Type": "application/json"})
    if method == "POST" and data is None:
        req.data = b""
    try:
        with urllib.request.urlopen(req, timeout=3) as r:
            raw = r.read().decode("utf-8")
            return r.status, (json.loads(raw) if raw.startswith("{") else raw)
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode("utf-8"))


def wait_for(cond, timeout=10.0):
    t = time.time() + timeout
    while time.time() < t:
        if cond():
            return True
        time.sleep(0.005)
    return False


class FakeRestrictor(object):
    def __init__(self):
        self.calls = []

    def pause(self):
        self.calls.append("pause")

    def resume(self):
        self.calls.append("resume")

    def stop(self, why=""):
        self.calls.append("stop:" + why)

    def snapshot(self):
        return {"active": True, "level": 1}


def case_g():
    print("G  POST /pause and /resume")
    seen = {}
    fakes = []

    def factory(event):
        fakes.append(FakeRestrictor())
        return fakes[-1]

    def during(app, clock):
        port = 18811
        srv = pomodoro.serve(app, "127.0.0.1", port)
        try:
            # 1) pause in the middle of the work period
            wait_for(lambda: app.snapshot()["phase"] == "work" and app.clock.now() > 300)
            code, j = http(port, "POST", "/pause", {"from": "test-g"})
            seen["pause_ok"] = code == 200 and j["state"]["paused"]
            s1 = app.snapshot()
            time.sleep(1.0)                    # 300 clock s if the clock were running
            s2 = app.snapshot()
            seen["frozen"] = (s1["now"] == s2["now"], s1["remaining_sec"], s2["remaining_sec"])
            seen["work_end_before"] = s1["phase_ends_at"]
            t_resume = time.time()
            code, j = http(port, "POST", "/resume", {"from": "test-g"})
            seen["resume_ok"] = code == 200 and j["resumed"] == "pause" and not j["state"]["paused"]
            # 2) pause in the middle of the prep: no tap may arrive while paused
            wait_for(lambda: app.snapshot()["phase"] == "prep")
            time.sleep(0.004)
            http(port, "POST", "/pause")
            time.sleep(0.05)  # a tap already on its way when the pause came (HTTP post of a few ms) may still land
            n_taps = len([e for e in seen["events"] if e["kind"] == "tap"])
            time.sleep(0.6)
            seen["prep_taps_during_pause"] = len([e for e in seen["events"] if e["kind"] == "tap"]) - n_taps
            http(port, "POST", "/resume")
            # 3) pause during the break: the restrictor (started at the break end of cycle 1? no: 2 cycles) ...
            wait_for(lambda: app.snapshot()["phase"] == "work" and app.snapshot()["cycle"] == 2)
            wait_for(lambda: bool(fakes))
            http(port, "POST", "/pause")
            http(port, "POST", "/resume")
            seen["t_resume"] = t_resume
        finally:
            srv.shutdown()
            srv.server_close()
    events = []
    seen["events"] = events
    clock = pomodoro.Clock(300.0)
    mock = Mock(18782, [clock])
    app = pomodoro.Pomodoro(ROUTE, "http://127.0.0.1:18782/", clock, 25 * 60, 5 * 60, allow_online=True, cycles=2,
                            say=lambda *a: None, restrictor_factory=factory)
    orig = app.event

    def rec(kind, **kw):
        r = orig(kind, **kw)
        events.append(r)
        return r
    app.event = rec
    th = threading.Thread(target=app.run)
    th.start()
    during(app, clock)
    th.join(timeout=60)
    mock.close()
    check(seen.get("pause_ok"), "POST /pause answered paused = true")
    fr = seen.get("frozen", (False, None, None))
    check(fr[0] and fr[1] == fr[2], "while paused the clock and the remaining time stood still (%s -> %s)" % fr[1:])
    check(seen.get("resume_ok"), "POST /resume left the pause")
    check(seen.get("prep_taps_during_pause") == 0, "no prep tap was sent while paused in the prep (%s)"
          % seen.get("prep_taps_during_pause"))
    w1 = phases(events, "work")[0]["ends_at"]
    check(abs(w1 - seen.get("work_end_before", -1)) < 0.01, "the end of work in clock time did not change (the pause moved it in real time only)")
    start = [e for e in events if e["kind"] == "tap" and e["role"] == "start" and e["cycle"] == 1]
    off = (start[0]["t"] - w1) / 300.0 if start else -1
    check(start and 0 <= off < 0.02, "the start tap still came %.4f real s after the end of work" % off)
    check(len(mock.taps) == 2 * (N_PREP + 1), "all %d taps arrived in the end (%d)" % (2 * (N_PREP + 1), len(mock.taps)))
    ctl = [e for e in events if e["kind"] == "control"]
    check(any(e["action"] == "pause" and e["by"] == "test-g" for e in ctl), "the pause is logged with from = test-g")
    check(any(e["action"] == "resume" and str(e["by"]).startswith("http 127.0.0.1") for e in ctl),
          "a control without from is logged with the client address")
    check(fakes and fakes[0].calls[:2] == ["pause", "resume"], "the restrictor was paused and resumed (%s)"
          % (fakes[0].calls if fakes else None))
    check(events[-1]["phase"] == "stopped", "stopped after 2 cycles")


def case_h():
    print("H  POST /reset")
    seen = {}
    events = []

    def during(app, clock):
        port = 18812
        srv = pomodoro.serve(app, "127.0.0.1", port)
        try:
            wait_for(lambda: app.snapshot()["phase"] == "work" and app.clock.now() > 200)
            http(port, "POST", "/pause")
            t = app.clock.now()
            code, j = http(port, "POST", "/reset", {"scope": "cycle", "from": "test-h"})
            seen["code"] = code
            wait_for(lambda: app.snapshot()["work_seq"] == 2)
            s = app.snapshot()
            seen["cycle_reset"] = (s["cycle"], s["paused"], round(s["remaining_sec"]), s["work_started_by"], t)
            # into cycle 2, then reset all
            wait_for(lambda: app.snapshot()["cycle"] == 2 and app.snapshot()["phase"] == "work")
            code, j = http(port, "POST", "/reset?scope=all&from=test-h2")
            wait_for(lambda: app.snapshot()["work_seq"] == 4)
            s = app.snapshot()
            seen["all_reset"] = (s["cycle"], s["work_started_by"])
            code, j = http(port, "POST", "/reset", {"scope": "nonsense"})
            seen["bad"] = code
            wait_for(lambda: app.snapshot()["phase"] == "work" and app.snapshot()["cycle"] == 2, timeout=20)
            http(port, "POST", "/stop", {"from": "test-h"})
        finally:
            srv.shutdown()
            srv.server_close()
    clock = pomodoro.Clock(300.0)
    app = pomodoro.Pomodoro(ROUTE, None, clock, 25 * 60, 5 * 60, allow_online=False, cycles=0,
                            say=lambda *a: None)
    orig = app.event

    def rec(kind, **kw):
        r = orig(kind, **kw)
        events.append(r)
        return r
    app.event = rec
    th = threading.Thread(target=app.run)
    th.start()
    during(app, clock)
    th.join(timeout=60)
    check(seen.get("code") == 200, "POST /reset answered 200")
    c = seen.get("cycle_reset")
    # the clock runs x300, so the snapshot taken a few real ms after the reset has 1-2 clock s less
    check(c and c[0] == 1 and not c[1] and 25 * 60 - 3 <= c[2] <= 25 * 60 and c[3] == "reset",
          "scope cycle: still cycle 1, not paused, 25:00 left again, started by reset (%s)" % (c,))
    a = seen.get("all_reset")
    check(a and a[0] == 1 and a[1] == "reset", "scope all: back to cycle 1 (%s)" % (a,))
    check(seen.get("bad") == 400, "an unknown scope is refused with 400")
    check(not th.is_alive() and events[-1]["phase"] == "stopped", "POST /stop ended the run")


def case_i():
    print("I  POST /config and the control page")
    seen = {}
    events = []

    def during(app, clock):
        port = 18813
        srv = pomodoro.serve(app, "127.0.0.1", port)
        try:
            code, page = http(port, "GET", "/")
            seen["page"] = code == 200 and "/pause" in page and "/config" in page and "viewport" in page
            wait_for(lambda: app.snapshot()["phase"] == "work" and app.clock.now() > 100)
            code, j = http(port, "POST", "/config", {"work": 10, "break": 2, "allow_online": True, "from": "test-i"})
            seen["cfg"] = (code, j.get("applied"), j["state"]["config"])
            seen["cur_end"] = app.snapshot()["phase_ends_at"]
            code, j = http(port, "POST", "/config", {"work": -3})
            seen["bad"] = code
            code, j = http(port, "POST", "/config", {"restrict": True})
            seen["no_restrict"] = code
        finally:
            srv.shutdown()
            srv.server_close()
    clock = pomodoro.Clock(300.0)
    mock = Mock(18783, [clock])
    app = pomodoro.Pomodoro(ROUTE, "http://127.0.0.1:18783/", clock, 25 * 60, 5 * 60, allow_online=False, cycles=2,
                            say=lambda *a: None)
    orig = app.event

    def rec(kind, **kw):
        r = orig(kind, **kw)
        events.append(r)
        return r
    app.event = rec
    th = threading.Thread(target=app.run)
    th.start()
    during(app, clock)
    th.join(timeout=60)
    mock.close()
    check(seen.get("page"), "GET / serves the control page (buttons call /pause and /config, phone viewport)")
    code, applied, cfg = seen.get("cfg", (0, None, {}))
    check(code == 200 and cfg.get("work_min") == 10 and cfg.get("break_min") == 2 and cfg.get("allow_online"),
          "POST /config answered with the new settings (%s)" % cfg)
    work = phases(events, "work")
    brk = phases(events, "break")
    # (the clock starts when the Pomodoro is made, so the first work period starts a few clock s after 0 at x300)
    check(abs(work[0]["ends_at"] - work[0]["t"] - 25 * 60) < 1, "the running work period kept its 25 min")
    check(abs((brk[0]["ends_at"] - work[0]["ends_at"]) - 120) < 1, "the next break used the new 2 min")
    check(abs((work[1]["ends_at"] - brk[0]["ends_at"]) - 600) < 1, "the next work period used the new 10 min")
    held = [e for e in events if e["kind"] == "prep_held"]
    check(not held and len(mock.taps) == 2 * (N_PREP + 1),
          "allow_online turned on during work applied from that prep (%d taps)" % len(mock.taps))
    check(seen.get("bad") == 400, "work = -3 is refused with 400")
    check(seen.get("no_restrict") == 400, "restrict cannot be turned on when restrict.py is not given (400)")
    check(any(e["kind"] == "control" and e["action"] == "config" and e["by"] == "test-i" and e.get("work") == 10
              for e in events), "the config change is logged with from and the values")


if __name__ == "__main__":
    print("pomodoro-timer %s" % pomodoro.VERSION)
    for f in (case_a, case_c, case_d, case_e, case_g, case_h, case_i, case_j, case_b):
        f()
    print("RESULT: %s" % ("all passed" if not FAILS else "%d failed" % len(FAILS)))
    sys.exit(1 if FAILS else 0)
