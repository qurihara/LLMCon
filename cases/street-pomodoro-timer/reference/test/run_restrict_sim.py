# -*- coding: utf-8 -*-
"""Simulation test for restrict.py (and its hook in pomodoro.py) with a mock MCPCon that answers get_state and
records the mapping / clear posts. Time is shortened: one level every 0.5 s, idle after 1.5 s.
  F1 while the player keeps touching the controller, the levels go up 1..5 (4 picks + timeup) and the rules
     stack (only the largest delay is kept);
  F2 pressing Start lifts every rule for the grace time;
  F3 when the player stops touching the controller, the restrictions end (idle) and a clear is sent;
  F4 pomodoro.py with a restrictor factory starts the restrictions when the break ends.
  F5 pause() (pomodoro.py POST /pause) lifts every rule and freezes the levels; resume() goes on from the same level.
  python3 test/run_restrict_sim.py
"""
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import restrict  # noqa: E402
import pomodoro  # noqa: E402

FAILS = []


def check(cond, msg):
    print(("  ok    " if cond else "  FAIL  ") + msg)
    if not cond:
        FAILS.append(msg)


class TS(ThreadingMixIn, HTTPServer):
    daemon_threads = True


class Mock(object):
    def __init__(self, port):
        self.posts, self.buttons, self.lx = [], [], 0
        mock = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _send(self, body, extra=None):
                data = body.encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                for k, v in (extra or {}).items():
                    self.send_header(k, v)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_POST(self):
                n = int(self.headers.get("Content-Length") or 0)
                body = json.loads(self.rfile.read(n) or b"{}")
                if self.path.startswith("/connect/apply"):
                    mock.posts.append(dict(body, at=time.time()))
                    return self._send("{}")
                m = body.get("method")
                if m == "initialize":
                    return self._send(json.dumps({"jsonrpc": "2.0", "id": body.get("id"), "result": {}}),
                                      {"Mcp-Session-Id": "mock"})
                if body.get("id") is None:
                    return self._send("")
                text = "ok"
                if m == "tools/call" and body["params"]["name"] == "get_state":
                    text = "buttons=[%s] LT=0 RT=0 LX=%d LY=0 RX=0 RY=0" % (",".join(mock.buttons) or "-", mock.lx)
                return self._send(json.dumps({"jsonrpc": "2.0", "id": body["id"], "result": {
                    "content": [{"type": "text", "text": text}]}}))

        self.srv = TS(("127.0.0.1", port), H)
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def close(self):
        self.srv.shutdown()
        self.srv.server_close()


def wiggle(mock, stop):
    """The player keeps moving the stick (and pressing X now and then)."""
    i = 0
    while not stop.is_set():
        i += 1
        mock.lx = 20000 if i % 2 else -20000
        mock.buttons = ["X"] if i % 3 == 0 else []
        time.sleep(0.15)
    mock.lx, mock.buttons = 0, []


def case_f123():
    print("F1-F3  restrict.py, one level every 0.5 s, idle 1.5 s, menu grace 0.8 s")
    mock = Mock(18790)
    steps = restrict.load_steps(restrict.DEFAULT_PLAN, restrict.DEFAULT_PICKS)
    events = []
    r = restrict.Restrictor("http://127.0.0.1:18790/", steps, step_sec=0.5, idle_sec=1.5, max_sec=30,
                            menu_grace=0.8, renew_sec=0.4, poll_sec=0.05,
                            event=lambda kind, **kw: events.append(dict(kw, kind=kind, at=time.time())))
    stop = threading.Event()
    th = threading.Thread(target=wiggle, args=(mock, stop), daemon=True)
    th.start()
    r.start()
    time.sleep(2.8)                           # levels 1..5
    lv = [e["level"] for e in events if e["kind"] == "restrict_level"]
    check(lv == [1, 2, 3, 4, 5], "levels went up 1..5 while the player kept playing (%s)" % lv)
    maps = [p for p in mock.posts if p.get("kind") == "mapping"]
    last = maps[-1]["rules"] if maps else []
    delays = [x for x in last if x["op"] == "delay"]
    check(len(delays) == 1 and delays[0]["delayMs"] == 400,
          "only the largest delay is kept at the top level (%s)" % [d["delayMs"] for d in delays])
    check(any(x["op"] == "remap" for x in last), "the stacked rules include the strong level's remap")
    check(all(p.get("durationSec", 0) > 0 for p in maps), "every mapping has a short lifetime")
    bad = [x for p in maps for x in p["rules"] if restrict.touches_protected(x)]
    check(not bad, "no mapping touches the menu button, the directions, confirm or cancel (%s)" % bad)
    # F2: press Start
    t_start = time.time()
    mock.buttons = ["Start"]
    time.sleep(0.3)
    snap = r.snapshot()
    check(snap.get("suspended") == "menu", "Start lifts the restrictions (suspended = menu)")
    clears = [p for p in mock.posts if p.get("kind") == "clear" and p["at"] > t_start]
    check(len(clears) >= 1, "a clear was sent after Start")
    mock.buttons = []
    time.sleep(1.0)
    check(r.snapshot().get("suspended") is None, "the restrictions came back after the grace time")
    # F3: stop playing
    stop.set()
    th.join()
    t_idle = time.time()
    time.sleep(2.2)
    snap = r.snapshot()
    check(snap.get("ended") == "idle" and not snap.get("active"), "ended as idle after the player stopped (%s)"
          % snap.get("ended"))
    ends = [e for e in events if e["kind"] == "restrict_end"]
    check(ends and 1.3 <= ends[0]["at"] - t_idle <= 2.2, "ended %.2f s after the last touch (idle 1.5 s)"
          % ((ends[0]["at"] - t_idle) if ends else -1))
    check(mock.posts[-1].get("kind") == "clear", "the last post to MCPCon is a clear")
    mock.close()


def case_f4():
    print("F4  pomodoro.py starts the restrictions at the end of the break")
    mock = Mock(18791)
    steps = restrict.load_steps(restrict.DEFAULT_PLAN, restrict.DEFAULT_PICKS)
    started = []

    def factory(event):
        r = restrict.Restrictor("http://127.0.0.1:18791/", steps, step_sec=0.5, idle_sec=0.6, max_sec=5,
                                poll_sec=0.05, event=event)
        r.start()
        started.append(time.time())
        return r
    route = pomodoro.load_route(os.path.join(os.path.dirname(HERE), "route.sf6_training.json"))
    clock = pomodoro.Clock(300.0)
    evs = []
    app = pomodoro.Pomodoro(route, "http://127.0.0.1:18791/", clock, 60, 30, allow_online=False, cycles=1,
                            say=lambda *a: None, restrictor_factory=factory)
    orig = app.event

    def rec(kind, **kw):
        r = orig(kind, **kw)
        evs.append(r)
        return r
    app.event = rec
    app.run()
    kinds = [e["kind"] for e in evs]
    check(len(started) == 1, "one Restrictor was started")
    check("restrict_start" in kinds and kinds.index("restrict_start") > kinds.index("break_end"),
          "restrict_start came after break_end")
    check("restrict_end" in kinds, "the restrictions were ended when the timer stopped (%s)"
          % [e.get("why") for e in evs if e["kind"] == "restrict_end"])
    mock.close()


def case_f5():
    print("F5  Restrictor.pause / resume")
    mock = Mock(18792)
    steps = restrict.load_steps(restrict.DEFAULT_PLAN, restrict.DEFAULT_PICKS)
    events = []
    r = restrict.Restrictor("http://127.0.0.1:18792/", steps, step_sec=0.5, idle_sec=5, max_sec=30, renew_sec=0.4,
                            poll_sec=0.05, event=lambda kind, **kw: events.append(dict(kw, kind=kind)))
    stop = threading.Event()
    th = threading.Thread(target=wiggle, args=(mock, stop), daemon=True)
    th.start()
    r.start()
    time.sleep(0.7)                      # level 2
    lv = r.snapshot()["level"]
    r.pause()
    t_p = time.time()
    time.sleep(1.5)                      # would be level 5 without the pause
    snap = r.snapshot()
    check(snap["level"] == lv and snap["suspended"] == "paused", "paused: the level stayed at %d (%s)"
          % (lv, snap["level"]))
    after = [p for p in mock.posts if p["at"] > t_p + 0.2]
    check(after and all(p.get("kind") == "clear" for p in after) or not after,
          "no mapping was sent while paused (%s)" % sorted(set(p.get("kind") for p in after)))
    r.resume()
    time.sleep(0.6)
    check(r.snapshot()["level"] == lv + 1 and not r.snapshot().get("suspended"),
          "resumed: one level up after 0.5 s more (%d)" % r.snapshot()["level"])
    stop.set()
    r.stop("test")
    mock.close()


if __name__ == "__main__":
    case_f123()
    case_f4()
    case_f5()
    print("RESULT: %s" % ("all passed" if not FAILS else "%d failed" % len(FAILS)))
    sys.exit(1 if FAILS else 0)
