# -*- coding: utf-8 -*-
"""Make MCPCon read the player's pad (in the lab, an arcade stick called pws) and set its axes, so that the stick drives SF6 through MCPCon.

SF6 only sees MCPCon's virtual pad (XInput 0), so the stick's input has to go through MCPCon; this is also what
lets restrict.py make the game harder after the break. MCPCon forgets the selection and the axes when it restarts,
so start_pomodoro.ps1 runs this every time.

  python setup_pad.py                      # pick the pad whose name contains "Panthera"
  python setup_pad.py --name "Razer" --mcpcon http://127.0.0.1:8777/

Axes of the pws (Razer Panthera (PS4), VID 1532, PID 0401), found with get_pad_raw on 2026-11-19: the left
stick is a0/a1, the right stick a2/a5, and the triggers L2/R2 are a3/a4 (they rest at 0). MCPCon's default
(ry=3, no triggers) read L2 as the right stick's vertical axis, which held the right stick up.
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from mcp_client import Mcp  # noqa: E402

# the lab's Razer Panthera (PS4); find the axes of your pad with MCPCon's get_pad_raw and pass --axes
AXES = {"lx": 0, "ly": 1, "rx": 2, "ry": 5, "lt": 3, "rt": 4, "invertY": True}


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--mcpcon", default="http://127.0.0.1:8777/")
    ap.add_argument("--name", default="Panthera", help="part of the pad's name shown by list_pads")
    ap.add_argument("--axes", default=None, help='JSON, e.g. {"lx":0,"ly":1,"rx":2,"ry":3,"invertY":true}')
    a = ap.parse_args(argv)
    axes = AXES if not a.axes else __import__("json").loads(a.axes)
    m = Mcp(a.mcpcon)
    pads = m.call("list_pads", {})
    print(pads)
    idx = None
    for line in pads.splitlines():
        mm = re.search(r"\[(\d+)\]\s*(.+)", line)
        if mm and a.name.lower() in mm.group(2).lower():
            idx = mm.group(1)
            break
    if idx is None:
        print("NG: no pad whose name contains '%s'. Is the stick plugged in?" % a.name)
        return 1
    print(m.call("select_pad", {"id": idx}))
    print(m.call("set_pad_axes", axes))
    state = m.call("get_state", {})
    print(state)
    if "RY=0 " not in state:
        print("note: the right stick is not at rest (RY is not 0); check the axes")
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
