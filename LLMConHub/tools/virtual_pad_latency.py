#!/usr/bin/env python3
"""Raspberry Pi の中に仮想のゲームパッド（uinput）を作り、決まった回数だけ A を押して離す。

LLMCon Hub の遅延を、人の手を使わずに測るための道具である。仮想のパッドは本物のパッドと同じ evdev の経路で
ホストに読まれるので、ホストの記録（USB は LLMCONHUB_USB_LOG、Bluetooth はブリッジの --log）の input_us が、
カーネルが事象に付けた時刻になる。この道具も、押した時刻（CLOCK_MONOTONIC）を CSV に書く。

使い方（root で動かす）：
  sudo python3 virtual_pad_latency.py --count 100 --out /var/tmp/vpad.csv
作ったあと、--wait 秒のあいだに MCP の select_pad で、この仮想のパッド（表示名 LLMCon Hub latency pad）を選んでおく。
python3-evdev は使わない（Raspberry Pi OS Lite には入っていない）。
"""
import argparse
import fcntl
import os
import random
import struct
import time

EV_SYN, EV_KEY, EV_ABS = 0x00, 0x01, 0x03
SYN_REPORT = 0
BTN_SOUTH, BTN_EAST, BTN_NORTH, BTN_WEST = 0x130, 0x131, 0x133, 0x134
ABS_X, ABS_Y = 0x00, 0x01
UI_SET_EVBIT, UI_SET_KEYBIT, UI_SET_ABSBIT = 0x40045564, 0x40045565, 0x40045567
UI_DEV_CREATE, UI_DEV_DESTROY = 0x5501, 0x5502
NAME = b"LLMCon Hub latency pad"


def emit(fd, etype, code, value):
    os.write(fd, struct.pack("llHHi", 0, 0, etype, code, value))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=100)
    ap.add_argument("--out", default="/var/tmp/vpad.csv")
    ap.add_argument("--wait", type=float, default=10.0, help="作ってから押し始めるまでの秒数（そのあいだに select_pad する）")
    a = ap.parse_args()

    fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
    for ev in (EV_KEY, EV_ABS, EV_SYN):
        fcntl.ioctl(fd, UI_SET_EVBIT, ev)
    for k in (BTN_SOUTH, BTN_EAST, BTN_NORTH, BTN_WEST):
        fcntl.ioctl(fd, UI_SET_KEYBIT, k)
    for ab in (ABS_X, ABS_Y):
        fcntl.ioctl(fd, UI_SET_ABSBIT, ab)
    absmax = [0] * 64; absmin = [0] * 64
    absmax[ABS_X] = absmax[ABS_Y] = 255
    dev = struct.pack("80sHHHHi", NAME, 0x03, 0x1209, 0x0001, 1, 0)
    dev += struct.pack("64i", *absmax) + struct.pack("64i", *absmin) + struct.pack("64i", *([0] * 64)) + struct.pack("64i", *([0] * 64))
    os.write(fd, dev)
    fcntl.ioctl(fd, UI_DEV_CREATE)
    time.sleep(0.5)
    # 軸を中央にしておく
    emit(fd, EV_ABS, ABS_X, 128); emit(fd, EV_ABS, ABS_Y, 128); emit(fd, EV_SYN, SYN_REPORT, 0)
    print(f"仮想のパッドを作りました。{a.wait} 秒後に押し始めます。", flush=True)
    time.sleep(a.wait)

    with open(a.out, "w") as f:
        f.write("trial,edge,sent_us\n")
        for i in range(1, a.count + 1):
            for edge, val in (("press", 1), ("release", 0)):
                t = time.monotonic_ns() // 1000
                emit(fd, EV_KEY, BTN_SOUTH, val); emit(fd, EV_SYN, SYN_REPORT, 0)
                f.write(f"{i},{edge},{t}\n")
                time.sleep(random.uniform(0.12, 0.30))
    print(f"{a.count} 回押しました。記録：{a.out}", flush=True)
    time.sleep(1.0)
    fcntl.ioctl(fd, UI_DEV_DESTROY)
    os.close(fd)


if __name__ == "__main__":
    main()
