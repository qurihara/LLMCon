# -*- coding: utf-8 -*-
"""After the break: make the game harder to play, step by step, so that the player goes back to work
(derived from Finalize Fantasy with LLMCon, ../finalize-fantasy/; Kurihara decided this on 2026-11-19).

When the break ends, the Restrictor walks up the levels of a Finalize Fantasy plan (light, medium, strong,
strongest, then the plan's "timeup" restriction), one level every step_sec seconds, and stacks the chosen
option of each level. In Finalize Fantasy the player chooses one option out of three; here the player is in
the middle of a match, so one option per level is chosen beforehand (--picks; the defaults avoid stick-axis
rules, because the player's arcade stick may arrive as the D-pad).

The restrictions are sent to MCPCon the same way as ff_server.py does: POST /connect/apply {"kind": "clear"}
then {"kind": "mapping", "rules": [...], "durationSec": ...}, renewed every renew_sec seconds with a short
lifetime, so they vanish by themselves within seconds if this program stops. Injected macros (the menu steps
of pomodoro.py) are not affected by the mapping rules.

The restrictions end when
  - the player has not touched the controller for idle_sec seconds (MCPCon get_state does not change), or
  - max_sec seconds have passed, or
  - stop() is called.
Pressing Start lifts every restriction for menu_grace seconds (as in Finalize Fantasy), so that the menus
after a match (leaving Casual Match) are never blocked.
"""
import json
import os
import re
import sys
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from mcp_client import Mcp  # noqa: E402

SOURCE = "pomodoro-restrict"
DEFAULT_PLAN = os.path.join(HERE, "plans", "street-fighter-6.json")
DEFAULT_PICKS = ["ちょい遅延", "強パンチ気まぐれ", "弱強パンチ入替", "超遅延"]
STATE_RE = re.compile(r"buttons=\[([^\]]*)\] LT=(\d+) RT=(\d+) LX=(-?\d+) LY=(-?\d+) RX=(-?\d+) RY=(-?\d+)")


def combine(options):
    """Same as finalize-fantasy brain.combine: no rule twice (an inversion given twice cancels itself out),
    and only the largest delay (delays do not add up in MCPCon)."""
    out, seen, delay = [], set(), None
    for o in options:
        for r in o["rules"]:
            if r["op"] == "delay":
                if delay is None or r["delayMs"] > delay["delayMs"]:
                    delay = r
                continue
            key = json.dumps(r, sort_keys=True)
            if key not in seen:
                seen.add(key)
                out.append(r)
    if delay:
        out.append(delay)
    return out


# SF6 Classic, the lab's setting (finalize-fantasy plans/street-fighter-6.json)
BUTTON_JA = {"X": "弱パンチ（X）", "Y": "中パンチ（Y）", "RB": "強パンチ（RB）", "A": "弱キック（A）",
             "B": "中キック（B）", "RT": "強キック（RT）", "LT": "LT", "LB": "LB",
             "DLeft": "十字キーの左", "DRight": "十字キーの右", "DUp": "十字キーの上", "DDown": "十字キーの下"}


def describe(step):
    """A plain, factual sentence for what the restriction does, made from its rules (not the plan's free text,
    which said things like "it feels as if ..."; Kurihara found that vague, 2026-11-19)."""
    out = []
    rules = step.get("rules", [])
    remaps = {(r.get("from"), r.get("to")) for r in rules if r["op"] == "remap"}
    done_swap = set()
    for r in rules:
        op = r["op"]
        b = BUTTON_JA.get(r.get("button") or r.get("axis") or "", r.get("button") or r.get("axis") or "")
        if op == "delay":
            out.append("すべての入力が%dミリ秒遅れて届きます" % r["delayMs"])
        elif op == "disable":
            out.append("%sが出ません" % b)
        elif op == "remap":
            a, c = r["from"], r["to"]
            if (c, a) in remaps:
                if frozenset((a, c)) in done_swap:
                    continue
                done_swap.add(frozenset((a, c)))
                out.append("%sと%sが入れ替わります" % (BUTTON_JA.get(a, a), BUTTON_JA.get(c, c)))
            else:
                out.append("%sを押すと%sになります" % (BUTTON_JA.get(a, a), BUTTON_JA.get(c, c)))
        elif op == "invert" and r.get("button"):
            out.append("%sが押しっぱなしになり、押している間だけ離れます" % b)
        elif op == "invert" and r.get("axis") == "LX":
            out.append("スティックの左右が逆になります")
        elif op == "invert":
            out.append("%sの向きが逆になります" % b)
        elif op == "clamp" and r.get("amount", 1) == 0:
            out.append("スティックで左右に動けません" if r.get("axis") == "LX" else "%sが出ません" % b)
        elif op == "clamp":
            out.append("%sの振れ幅が%d%%までになります" % (b, int(r["amount"] * 100)))
        elif op == "deadzone":
            out.append("%sは深く押し込まないと出ません" % b)
        elif op == "rotate":
            out.append("スティックの向きが%d度ずれます" % int(r["amount"]))
        elif op == "rate":
            out.append("スティックの動きが遅れてついてきます")
        elif op == "swap":
            out.append("スティックの縦と横が入れ替わります")
        else:
            out.append("%sの操作が変わります" % b)
    text = "。".join(out)
    on, off = step.get("pulse_on_sec", 0), step.get("pulse_off_sec", 0)
    if on and off:
        text += "。この制限は%g秒掛かり、%g秒外れる、を繰り返します" % (on, off)
    return text + "。" if text else ""


# Inputs that must always work, so that the player can still leave Casual Match through the menu after the
# match (Kurihara, 2026-11-19): the menu button, the directions (the arcade stick may arrive as the D-pad or
# as the left stick, so both), confirm and cancel. Delays are kept: the menus stay usable, only slower.
PROTECTED_BUTTONS = {"Start", "Back", "A", "B", "DUp", "DDown", "DLeft", "DRight"}
PROTECTED_AXES = {"LS", "LX", "LY"}


def touches_protected(rule):
    if rule["op"] == "delay":
        return False
    names = {rule.get("button"), rule.get("axis"), rule.get("from"), rule.get("to")}
    return bool(names & PROTECTED_BUTTONS or names & PROTECTED_AXES)


def protect(step):
    """The step without the rules that touch a protected input; also returns the dropped rules."""
    kept = [r for r in step.get("rules", []) if not touches_protected(r)]
    dropped = [r for r in step.get("rules", []) if touches_protected(r)]
    return dict(step, rules=kept), dropped


def load_steps(plan_path, picks):
    """The options to stack, in order: the picked option of each level, then the plan's timeup option.
    Rules that touch a protected input (menu, directions, confirm, cancel) are removed; a step left with no
    rule is an error, so that a pick never silently does nothing."""
    with open(plan_path, encoding="utf-8") as f:
        plan = json.load(f)["plan"]
    steps = []
    for lv, pick in zip(plan["levels"], picks):
        opts = {o["label_ja"]: o for o in lv["options"]}
        if pick not in opts:
            raise ValueError("level %s has no option '%s' (has: %s)" % (lv["level"], pick, ", ".join(opts)))
        steps.append(dict(opts[pick], level=lv["level"], level_ja=lv["name_ja"]))
    if plan.get("timeup"):
        steps.append(dict(plan["timeup"], level=len(steps) + 1, level_ja="終了時間"))
    out = []
    for st in steps:
        st, dropped = protect(st)
        if not st["rules"]:
            raise ValueError("'%s' only restricts protected inputs (menu, directions, confirm, cancel); pick another"
                             % st["label_ja"])
        st["dropped_rules"] = dropped
        st["describe_ja"] = describe(st)
        out.append(st)
    return out


def post_json(url, payload, timeout=2.0):
    req = urllib.request.Request(url, data=json.dumps(payload).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


class Restrictor(object):
    def __init__(self, mcpcon, steps, step_sec=30.0, idle_sec=15.0, max_sec=600.0, menu_grace=20.0,
                 renew_sec=2.0, dry_run=False, event=None, poll_sec=0.25):
        self.url = mcpcon.rstrip("/")
        self.steps = steps
        self.step_sec, self.idle_sec, self.max_sec = float(step_sec), float(idle_sec), float(max_sec)
        self.menu_grace, self.renew_sec, self.poll_sec = float(menu_grace), float(renew_sec), float(poll_sec)
        self.dry_run = dry_run
        self.event = event or (lambda kind, **kw: print(kind, kw, flush=True))
        self.mcp = Mcp(self.url + "/")
        self.stop_ev = threading.Event()
        self.thread = None
        self.lock = threading.Lock()
        self.state = {"active": False, "level": 0, "labels": [], "suspended": None, "ended": None}
        self.sent, self.sent_at = None, 0.0
        self.paused = False  # set by pomodoro.py's POST /pause: no rule is sent and the clock of the levels stops

    # -- MCPCon ---------------------------------------------------------------------------------------
    def apply(self, rules):
        key = json.dumps(rules, sort_keys=True)
        if key == self.sent and time.time() - self.sent_at < self.renew_sec:
            return
        if not self.dry_run:
            try:
                if self.sent not in (None, "[]") or not rules:
                    post_json(self.url + "/connect/apply", {"kind": "clear", "source": SOURCE})
                if rules:
                    post_json(self.url + "/connect/apply", {"kind": "mapping", "rules": rules,
                              "durationSec": round(self.renew_sec * 2.5, 2), "from": SOURCE})
            except Exception as e:
                self.event("restrict_error", error=str(e))
                self.sent = None
                return
        self.sent, self.sent_at = key, time.time()

    def read_state(self):
        try:
            text = self.mcp.call("get_state", {}, timeout=2)
        except Exception:
            return None
        m = STATE_RE.search(text or "")
        if not m:
            return None
        buttons = frozenset(b for b in m.group(1).split(",") if b and b != "-")
        axes = [int(x) for x in m.groups()[1:]]
        # quantise the sticks and triggers so that sensor noise is not taken as the player touching them
        return buttons, tuple(int(v / 4096) for v in axes)

    # -- the loop -------------------------------------------------------------------------------------
    def start(self):
        self.stop_ev.clear()
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def stop(self, why="stopped"):
        self.stop_ev.set()
        if self.thread:
            self.thread.join(timeout=3)
        self._end(why)

    def _end(self, why):
        with self.lock:
            if self.state["ended"]:
                return
            self.state.update(active=False, ended=why)
        if not self.dry_run:
            try:
                post_json(self.url + "/connect/apply", {"kind": "clear", "source": SOURCE})
            except Exception:
                pass
        self.sent = "[]"
        self.event("restrict_end", why=why)

    def pause(self):
        self.paused = True

    def resume(self):
        self.paused = False

    def snapshot(self):
        with self.lock:
            return dict(self.state)

    def run(self):
        t0 = time.time()
        last_touch, prev, menu_until, level = t0, None, 0.0, 0
        with self.lock:
            self.state.update(active=True, level=0, labels=[], suspended=None, ended=None)
        self.event("restrict_start", steps=[s["label_ja"] for s in self.steps], step_sec=self.step_sec)
        last_loop = t0
        while not self.stop_ev.is_set():
            now = time.time()
            if self.paused:
                # the timer is paused: lift every rule, and shift the clocks so that the levels, the idle time and
                # the menu grace continue where they were when the timer resumes
                dt = now - last_loop
                t0, last_touch, menu_until = t0 + dt, last_touch + dt, menu_until + dt
                last_loop = now
                with self.lock:
                    self.state.update(suspended="paused")
                self.apply([])
                self.stop_ev.wait(self.poll_sec)
                continue
            last_loop = now
            st = self.read_state()
            if st is not None:
                if prev is not None and st != prev:
                    last_touch = now
                    if "Start" in st[0] and "Start" not in prev[0]:
                        menu_until = now + self.menu_grace
                        self.event("restrict_menu", grace_sec=self.menu_grace)
                prev = st
            if now - last_touch >= self.idle_sec:
                return self._end("idle")
            if now - t0 >= self.max_sec:
                return self._end("max")
            new_level = min(len(self.steps), 1 + int((now - t0) / self.step_sec))
            if new_level != level:
                level = new_level
                s = self.steps[level - 1]
                self.event("restrict_level", level=level, level_ja=s["level_ja"], label=s["label_ja"],
                           explain=s.get("describe_ja"))
            opts = []
            for s in self.steps[:level]:
                on, off = s.get("pulse_on_sec", 0), s.get("pulse_off_sec", 0)
                if on and off and (now % (on + off)) >= on:
                    continue
                opts.append(s)
            suspended = "menu" if now < menu_until else None
            with self.lock:
                self.state.update(level=level, labels=[s["label_ja"] for s in self.steps[:level]],
                                  level_ja=self.steps[level - 1]["level_ja"], suspended=suspended,
                                  explain=self.steps[level - 1].get("describe_ja"),
                                  idle_left=round(max(0.0, self.idle_sec - (now - last_touch)), 1))
            self.apply([] if suspended else combine(opts))
            self.stop_ev.wait(self.poll_sec)
        self._end("stopped")
