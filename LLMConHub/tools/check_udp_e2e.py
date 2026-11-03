#!/usr/bin/env python3
"""LLMConHub のホストが、改変を掛けた状態を UDP の電文で出すかを、端から端まで確かめる。

前もって、LLMConHub のホストを、UDP の送り先をこの道具の待ち受けに向けて起動しておく。
  dotnet run --project LLMConHub/src/LLMConHub.Host -- --port 8790 --sink udp=127.0.0.1:9101

この道具は次の3つを確かめる。
  1. ウェブ版コントローラの経路（人間入力）で A を押すと、電文に A が出る。
  2. MCP の set_mapping で A を B に差し替えると、同じ操作で電文に B が出る（改変の規則が効く）。
  3. 人間入力を送ってから電文を受け取るまでの時間を、試行の回数（既定20回）だけ測る。
     mac の上では、物理のパッドの代わりにウェブ版コントローラの WebSocket を使うので、
     この時間には WebSocket の受け渡しが含まれる。Raspberry Pi の上の evdev の経路の遅延とは別物である。
"""
import argparse
import os
import socket
import statistics
import struct
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools", "mac"))
from llmcon import Mcp, VCon  # noqa: E402

FRAME = struct.Struct("<2sBBIQQHBBhhhh")
A, B = 0x1000, 0x2000


def wait_frame(sock, pred, timeout=1.0):
    """条件に合う状態の電文（定期の電文を除く）を待つ。受け取った時刻と電文を返す。"""
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        sock.settimeout(max(0.001, end - time.monotonic()))
        try:
            data, _ = sock.recvfrom(64)
        except socket.timeout:
            break
        t = time.monotonic_ns()
        f = FRAME.unpack_from(data)
        if f[0] != b"LB" or (f[2] & 1):
            continue
        if pred(f[6]):
            return t, f
    return None, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8790)
    ap.add_argument("--listen", type=int, default=9101)
    ap.add_argument("--trials", type=int, default=20)
    a = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", a.listen))
    m = Mcp(a.host, a.port, name="llmconhub-check")
    v = VCon(a.host, a.port)
    m.call("set_mapping", rules=[])
    time.sleep(0.3)

    ok = True
    # 1. 素通し
    v.press("A")
    t, f = wait_frame(sock, lambda b: b & A)
    print("1 素通しで A が出る:", "はい" if f else "いいえ")
    ok &= f is not None
    v.release("A")
    wait_frame(sock, lambda b: b == 0)

    # 2. 差し替え
    print(m.call("set_mapping", rules=[{"op": "remap", "from": "A", "to": "B"}]))
    time.sleep(0.2)
    v.press("A")
    t, f = wait_frame(sock, lambda b: b != 0)
    got_b = f is not None and (f[6] & B) and not (f[6] & A)
    print("2 A を B に差し替えると B が出る:", "はい" if got_b else f"いいえ（{f}）")
    ok &= bool(got_b)
    v.release("A")
    wait_frame(sock, lambda b: b == 0)
    m.call("set_mapping", rules=[])
    time.sleep(0.2)

    # 3. 時間の測定
    lat = []
    for i in range(a.trials):
        t0 = time.monotonic_ns()
        v.press("A")
        t1, f = wait_frame(sock, lambda b: b & A)
        if t1:
            lat.append((t1 - t0) / 1e6)
        v.release("A")
        wait_frame(sock, lambda b: b == 0)
        time.sleep(0.05)
    if lat:
        print(f"3 人間入力（WebSocket）から UDP の電文まで: 試行 {len(lat)}/{a.trials} 回、"
              f"中央値 {statistics.median(lat):.2f} ミリ秒、平均 {statistics.mean(lat):.2f} ミリ秒、"
              f"最小 {min(lat):.2f}、最大 {max(lat):.2f}")
        print("  各回:", " ".join(f"{x:.2f}" for x in lat))
    ok &= len(lat) == a.trials
    print("結果:", "合格" if ok else "不合格")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
