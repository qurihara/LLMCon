# -*- coding: utf-8 -*-
"""Lower the volume of Street Fighter 6 during work and give it back during the break (Kurihara, 2026-11-20).

Windows keeps one volume per application (the audio session of StreetFighter6.exe; the "Volume mixer").
This module sets that session's volume through pycaw (which needs comtypes and psutil):

    pip install pycaw

When pycaw is missing, or on a machine that is not Windows (the simulation on the lab's Mac), every call does nothing
and one warning is written; the timer is never stopped by the volume. The work is done in one background
thread (COM is initialised there), so that a slow audio API never delays the exact start tap of pomodoro.py.

The session exists only while SF6 plays a sound, so when it is not found the thread tries again every
retry_sec seconds until it is found once for the current target. The player's own change in the mixer is not
overridden until the next phase changes the target.
"""
import sys
import threading

IS_WIN = sys.platform.startswith("win")
INSTALL_HINT = "pip install pycaw   (pycaw brings comtypes and psutil)"


class AppVolume(object):
    def __init__(self, process="StreetFighter6.exe", enabled=True, event=None, retry_sec=5.0):
        self.process = process.lower()
        self.event = event or (lambda kind, **kw: print(kind, kw, flush=True))
        self.retry_sec = float(retry_sec)
        self.cv = threading.Condition()
        self.target = None      # 0.0 .. 1.0, or None for "do nothing"
        self.done_for = None    # the target that has been applied to at least one session
        self.changed = False    # True once this program has set a volume (then it restores 1.0 at the end)
        self.thread = None
        self.available = False
        self.reason = None
        self.enabled = enabled
        if not enabled:
            self.reason = "disabled (--no-volume)"
        elif not IS_WIN:
            self.reason = "not Windows"
        else:
            try:
                import pycaw.pycaw  # noqa: F401
                self.available = True
            except Exception as e:
                self.reason = "pycaw is not installed (%s); install: %s" % (e, INSTALL_HINT)
        if enabled and not self.available:
            self.event("volume_unavailable", reason=self.reason)

    def snapshot(self):
        return {"available": self.available, "enabled": self.enabled, "reason": self.reason,
                "target": self.target, "applied": self.done_for is not None and self.done_for == self.target}

    def set(self, level):
        """Ask for the SF6 volume level (0.0 .. 1.0). Returns at once."""
        if not (self.enabled and self.available):
            return
        with self.cv:
            self.target = max(0.0, min(1.0, float(level)))
            self.cv.notify()
            if self.thread is None:
                self.thread = threading.Thread(target=self._run, daemon=True)
                self.thread.start()

    def restore(self, timeout=3.0):
        """Give the volume back (1.0) and wait a little for it; used when the timer stops."""
        if not (self.available and self.changed):
            return
        self.set(1.0)
        with self.cv:
            self.cv.wait_for(lambda: self.done_for == 1.0, timeout=timeout)

    def set_enabled(self, on):
        if on == self.enabled:
            return
        if not on:
            self.restore()
            self.enabled = False
        else:
            self.enabled = True
            if self.reason == "disabled (--no-volume)":
                self.__init__(self.process, True, self.event, self.retry_sec)

    # -- the background thread --------------------------------------------------------------------------
    def _apply(self, level):
        from pycaw.pycaw import AudioUtilities
        n = 0
        for s in AudioUtilities.GetAllSessions():
            try:
                if s.Process is not None and s.Process.name().lower() == self.process:
                    s.SimpleAudioVolume.SetMasterVolume(level, None)
                    n += 1
            except Exception:
                continue
        return n

    def _run(self):
        try:
            import comtypes
            comtypes.CoInitialize()
        except Exception as e:
            self.event("volume_error", error="CoInitialize: %s" % e)
        while True:
            with self.cv:
                while self.target is None or self.target == self.done_for:
                    self.cv.wait()
                level = self.target
            try:
                n = self._apply(level)
            except Exception as e:
                n = 0
                self.event("volume_error", error=str(e))
            if n:
                with self.cv:
                    self.done_for = level
                    self.changed = True
                    self.cv.notify_all()
                self.event("volume", level=round(level, 2), sessions=n)
            else:
                with self.cv:  # SF6 has no audio session yet (not running, or silent); try again later
                    self.cv.wait(self.retry_sec)
