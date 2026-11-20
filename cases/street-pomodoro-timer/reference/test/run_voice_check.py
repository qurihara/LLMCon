# -*- coding: utf-8 -*-
"""Check the VOICEVOX voice files against what overlay.py really says (no game, no sound).
  V1 voice/index.json: every file exists, is a 16-bit mono WAV of a sensible length.
  V2 index.json has exactly the sentences of overlay.all_sentences (with the default restrictions).
  V3 a whole run of pomodoro.py (x300 clock; 25/5 min, 2 cycles, a pause, a reset, restriction levels 1..5 from a
     fake restrictor) is fed to overlay.Announcer the way overlay.py's tick does; every sentence it says must have
     a WAV (0 sentences without one), and the R2 sentence "作業を始めます。25分です" must be said exactly once at
     the start (not again after the break, which says "休憩終わり。作業に戻りましょう").
  python3 test/run_voice_check.py
"""
import json
import os
import sys
import threading
import time
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
import overlay  # noqa: E402
import pomodoro  # noqa: E402
import restrict  # noqa: E402

FAILS = []


def check(cond, msg):
    print(("  ok    " if cond else "  FAIL  ") + msg)
    if not cond:
        FAILS.append(msg)


def v1_v2():
    print("V1-V2  voice/index.json")
    with open(os.path.join(ROOT, "voice", "index.json"), encoding="utf-8") as f:
        idx = json.load(f)
    bad, lengths = [], []
    for text, name in idx["sentences"].items():
        try:
            with wave.open(os.path.join(ROOT, "voice", name)) as w:
                sec = w.getnframes() / float(w.getframerate())
                if w.getnchannels() != 1 or w.getsampwidth() != 2 or not 0.4 <= sec <= 8:
                    bad.append((text, w.getnchannels(), w.getsampwidth(), sec))
                lengths.append(sec)
        except Exception as e:
            bad.append((text, str(e)))
    check(not bad, "%d WAV files, all 16-bit mono, 0.4-8 s (%s)" % (len(lengths), bad[:3]))
    print("        lengths: %.2f .. %.2f s, total %.1f s; credit: %s" % (min(lengths), max(lengths), sum(lengths),
                                                                         idx.get("credit")))
    labels = [s["label_ja"] for s in restrict.load_steps(restrict.DEFAULT_PLAN, restrict.DEFAULT_PICKS)]
    want = set(overlay.all_sentences(labels))
    have = set(idx["sentences"])
    check(want == have, "index.json has exactly overlay.all_sentences (missing %s, extra %s)"
          % (sorted(want - have)[:3], sorted(have - want)[:3]))
    return have, labels


class FakeRestrictor(object):
    """Walks the default levels one per 0.3 real s, then ends; snapshot() looks like restrict.Restrictor's."""

    def __init__(self, steps):
        self.steps, self.t0, self.paused = steps, time.time(), False

    def snapshot(self):
        k = int((time.time() - self.t0) / 0.3)
        if k >= len(self.steps) + 2:
            return {"active": False, "level": len(self.steps), "ended": "idle"}
        lv = min(len(self.steps), k + 1)
        st = self.steps[lv - 1]
        return {"active": True, "level": lv, "labels": [s["label_ja"] for s in self.steps[:lv]],
                "level_ja": st["level_ja"], "explain": st["describe_ja"],
                "suspended": "menu" if k == len(self.steps) else None}

    def pause(self):
        self.paused = True

    def resume(self):
        self.paused = False

    def stop(self, why=""):
        pass


def v3(have):
    print("V3  what overlay.Announcer says during a whole run")
    steps = restrict.load_steps(restrict.DEFAULT_PLAN, restrict.DEFAULT_PICKS)
    said = []
    ann = overlay.Announcer(status=lambda text, sec=6: None, say_fn=said.append, beep_fn=lambda *a: None)
    app = pomodoro.Pomodoro(pomodoro.load_route(os.path.join(ROOT, "route.sf6_training.json")), None,
                            pomodoro.Clock(300.0), 25 * 60, 5 * 60, cycles=2, say=lambda *a: None,
                            restrictor_factory=lambda event: FakeRestrictor(steps))
    th = threading.Thread(target=app.run)
    th.start()
    last = {"phase": None}
    done = {"pause": False, "reset": False}
    t_end = time.time() + 60
    while th.is_alive() and time.time() < t_end:
        s = json.loads(json.dumps(app.snapshot()))  # the same JSON overlay.py receives
        if last["phase"] is not None and s["phase"] != last["phase"]:
            ann.phase_changed(last["phase"], s["phase"], s)
        last["phase"] = s["phase"]
        ann.update(s)
        ann.restrict_update(s.get("restrict"))
        if not done["pause"] and s["phase"] == "work" and s["cycle"] == 1 and s["now"] > 400:
            app.do_pause("test")  # resumed below after a few polls, so that the announcer sees the pause
            done["pause"] = time.time()
        if done["pause"] and done["pause"] is not True and time.time() - done["pause"] > 0.2:
            app.do_resume("test")
            done["pause"] = True
        if not done["reset"] and s["phase"] == "work" and s["cycle"] == 2 and s["now"] > 2400:
            app.do_reset("cycle", "test")
            done["reset"] = True
        time.sleep(0.004)
    th.join(timeout=5)
    missing = sorted(set(t for t in said if t not in have))
    print("        said %d times, %d different sentences" % (len(said), len(set(said))))
    check(not missing, "every sentence said has a WAV (without: %s)" % missing)
    check(said.count("作業を始めます。25分です") == 1, "R2 sentence said once at the start (%d)"
          % said.count("作業を始めます。25分です"))
    check(said.count("作業をやり直します。25分です") == 1, "the reset said 作業をやり直します once")
    check(said.count(overlay.S_BACK_TO_WORK) == 1, "the break end said 休憩終わり once (not with the R2 sentence)")
    for t in (overlay.S_PAUSE, overlay.S_RESUME, overlay.S_BREAK, overlay.S_LIFTED):
        check(t in said, "said: %s" % t)
    check(all((overlay.S_HARDER % s["label_ja"]) in said for s in steps), "said every restriction level")


if __name__ == "__main__":
    have, labels = v1_v2()
    v3(have)
    print("RESULT: %s" % ("all passed" if not FAILS else "%d failed" % len(FAILS)))
    sys.exit(1 if FAILS else 0)
