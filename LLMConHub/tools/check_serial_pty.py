#!/usr/bin/env python3
"""シリアルの出力先（--sink serial=...）を、擬似端末（pty）で端から端まで確かめる。

本物の出力用の機器（Pico や ESP32）の代わりに、この道具が擬似端末の片側で電文を受け取る。
この道具は、LLMConHub のホストを自分で起動し、ウェブ版コントローラの経路で A を押し、
シリアルの側に A の電文が COBS と CRC-8 の区切りで届くかを確かめ、届くまでの時間を試行の回数だけ測る。

使い方：
  python3 tools/check_serial_pty.py --dll src/LLMConHub.Host/bin/Debug/net8.0/llmconhub-host.dll
"""
import argparse
import os
import pty
import select
import statistics
import struct
import subprocess
import sys
import termios
import time
import tty

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools", "mac"))
from llmcon import VCon  # noqa: E402

FRAME = struct.Struct("<2sBBIQQHBBhhhh")
A = 0x1000


def crc8(data):
    crc = 0
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if crc & 0x80 else (crc << 1) & 0xFF
    return crc


def cobs_decode(src):
    out = bytearray()
    i = 0
    while i < len(src):
        code = src[i]
        i += 1
        if code == 0:
            return None
        out += src[i:i + code - 1]
        i += code - 1
        if code < 0xFF and i < len(src):
            out.append(0)
    return bytes(out)


class Reader:
    def __init__(self, fd):
        self.fd = fd
        self.buf = bytearray()
        self.bad = 0

    def frames(self, timeout):
        """届いた電文を (受け取った時刻, ボタン, 定期かどうか) の列で返す。"""
        out = []
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            r, _, _ = select.select([self.fd], [], [], max(0.0, end - time.monotonic()))
            if not r:
                break
            t = time.monotonic_ns()
            self.buf += os.read(self.fd, 4096)
            while 0 in self.buf:
                k = self.buf.index(0)
                chunk, self.buf = bytes(self.buf[:k]), self.buf[k + 1:]
                raw = cobs_decode(chunk)
                if raw is None or len(raw) != 37 or crc8(raw[:36]) != raw[36]:
                    self.bad += 1
                    continue
                f = FRAME.unpack_from(raw)
                out.append((t, f[6], bool(f[2] & 1)))   # f[2] が旗、f[3] は通し番号
            if out:
                break
        return out


def wait_buttons(rd, pred, timeout=1.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        for t, b, hb in rd.frames(end - time.monotonic()):
            if not hb and pred(b):
                return t
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", required=True)
    ap.add_argument("--port", type=int, default=8791)
    ap.add_argument("--trials", type=int, default=20)
    a = ap.parse_args()

    master, slave = pty.openpty()
    tty.setraw(master)
    slave_name = os.ttyname(slave)
    host = subprocess.Popen(["dotnet", a.dll, "--port", str(a.port), "--sink", f"serial={slave_name}"],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        time.sleep(4)
        rd = Reader(master)
        v = VCon("127.0.0.1", a.port)
        ok = True
        v.press("A")
        t = wait_buttons(rd, lambda b: b & A)
        print("1 シリアルに A の電文が届く:", "はい" if t else "いいえ")
        ok &= t is not None
        v.release("A")
        wait_buttons(rd, lambda b: b == 0)

        lat = []
        for _ in range(a.trials):
            t0 = time.monotonic_ns()
            v.press("A")
            t1 = wait_buttons(rd, lambda b: b & A)
            if t1:
                lat.append((t1 - t0) / 1e6)
            v.release("A")
            wait_buttons(rd, lambda b: b == 0)
            time.sleep(0.05)
        if lat:
            print(f"2 人間入力（WebSocket）からシリアルの電文まで: 試行 {len(lat)}/{a.trials} 回、"
                  f"中央値 {statistics.median(lat):.2f} ミリ秒、平均 {statistics.mean(lat):.2f}、最小 {min(lat):.2f}、最大 {max(lat):.2f}")
            print("  各回:", " ".join(f"{x:.2f}" for x in lat))
        print("CRC か COBS が合わなかった塊の数:", rd.bad)
        ok &= len(lat) == a.trials and rd.bad == 0
        print("結果:", "合格" if ok else "不合格")
        return 0 if ok else 1
    finally:
        host.terminate()
        try:
            host.wait(5)
        except subprocess.TimeoutExpired:
            host.kill()


if __name__ == "__main__":
    sys.exit(main())
