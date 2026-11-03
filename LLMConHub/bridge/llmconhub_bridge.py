#!/usr/bin/env python3
"""LLMConHub の橋渡しのプログラム。

LLMCon のホスト（llmconhub-host）が UDP で送る電文（PadFrame、36バイト）を受け取り、
Bluetooth のコントローラとして振る舞う既存の実装へ渡す。

受け渡し先（--backend）は次のとおりである。
  dump  受け取った状態を表示するだけ。Bluetooth を使わない。動作の確認と遅延の測定に使う。
  nxbt  nxbt（https://github.com/Brikwerk/nxbt）で Nintendo Switch の Pro コントローラとして振る舞う。
        Raspberry Pi の上で root の権限で動かす必要がある（nxbt が BlueZ の設定を書き換えるため）。

--log に CSV のファイルを渡すと、状態が変わった電文ごとに、次の時刻を記録する。どれも同じ単調増加の時計
（Linux では CLOCK_MONOTONIC）のマイクロ秒である。
  input_us  物理のパッドの入力の時刻（evdev の事象の時刻）。物理のパッドを読んでいないときは 0。
  sent_us   LLMCon のホストが電文を送った時刻。
  recv_us   このプログラムが電文を受け取った時刻。
  handed_us このプログラムが受け渡し先（nxbt など）へ状態を渡し終えた時刻。
これを tools/latency_summary.py に渡すと、遅延の指標を計算する。

電文の並びは src/LLMConHub/PadFrame.cs の注釈にある。並びを変えたら、両方を直すこと。
"""

import argparse
import os
import csv
import socket
import struct
import sys
import time

FRAME = struct.Struct("<2sBBIQQHBBhhhh")
assert FRAME.size == 36

# XInput と同じボタンのビットの並び（CntlLevelConnection.Core の ControllerEngine.ButtonBits と同じ）
BITS = {
    "DUp": 0x0001, "DDown": 0x0002, "DLeft": 0x0004, "DRight": 0x0008,
    "Start": 0x0010, "Back": 0x0020, "LS": 0x0040, "RS": 0x0080,
    "LB": 0x0100, "RB": 0x0200, "Guide": 0x0400,
    "A": 0x1000, "B": 0x2000, "X": 0x4000, "Y": 0x8000,
}


def now_us():
    return time.monotonic_ns() // 1000


def decode(data):
    """電文を辞書にする。識別の印か版が合わなければ None を返す。"""
    if len(data) < FRAME.size:
        return None
    magic, ver, flags, seq, sent, inp, buttons, lt, rt, lx, ly, rx, ry = FRAME.unpack_from(data)
    if magic != b"LB" or ver != 1:
        return None
    return {
        "heartbeat": bool(flags & 1), "seq": seq, "sent_us": sent, "input_us": inp,
        "buttons": buttons, "lt": lt, "rt": rt, "lx": lx, "ly": ly, "rx": rx, "ry": ry,
    }


def names(mask):
    return [n for n, b in BITS.items() if mask & b]


class DumpBackend:
    """受け取った状態を表示するだけの受け渡し先。"""

    def __init__(self, quiet=False):
        self.quiet = quiet

    def hand(self, f):
        if not self.quiet and not f["heartbeat"]:
            print(f"seq={f['seq']:>7} buttons={'+'.join(names(f['buttons'])) or '-':<20} "
                  f"LX={f['lx']:>6} LY={f['ly']:>6} RX={f['rx']:>6} RY={f['ry']:>6} LT={f['lt']:>3} RT={f['rt']:>3}",
                  flush=True)

    def close(self):
        pass


class NxbtBackend:
    """nxbt で Switch の Pro コントローラとして振る舞う受け渡し先。

    ボタンは、Xbox の位置に合わせた LLMCon の名前を、Switch の同じ位置のボタンへ割り当てる。
    下の A は Switch の B、右の B は Switch の A、左の X は Switch の Y、上の Y は Switch の X になる。
    名前ではなく位置で合わせるのは、ゲームの側の操作の説明が、ボタンの位置で書かれていることが多いからである。
    --by-label を付けると、名前で合わせる（A は A）。

    nxbt は Pro コントローラの報告を毎秒約132回送る（nxbt の controller/server.py）。そのため、ここで状態を
    渡してから Switch に届くまでに、最大で約7.6ミリ秒の待ちが加わる。これは Bluetooth の報告の周期による
    もので、LLMCon の側では縮められない。
    """

    POSITIONAL = {"A": "B", "B": "A", "X": "Y", "Y": "X"}
    LABEL = {"A": "A", "B": "B", "X": "X", "Y": "Y"}

    def __init__(self, by_label=False, reconnect=None):
        import nxbt  # Raspberry Pi の上でだけ入れる。pip install nxbt
        self.nxbt = nxbt
        self.face = self.LABEL if by_label else self.POSITIONAL
        self.nx = nxbt.Nxbt(debug=bool(os.environ.get("NXBT_DEBUG")))  # NXBT_DEBUG=1 で nxbt の詳しい記録を出す
        kwargs = {}
        if reconnect:
            kwargs["reconnect_address"] = reconnect if reconnect != "auto" else self.nx.get_switch_addresses()
        print("Switch の「持ちかた/順番を変える」の画面を開いてください。接続を待っています。", flush=True)
        self.index = self.nx.create_controller(nxbt.PRO_CONTROLLER, **kwargs)
        self.nx.wait_for_connection(self.index)
        # nxbt.set_controller_input は、別のプロセス（multiprocessing の Manager）との往復を毎回3回ほど行い、
        # Raspberry Pi 4 では1回の受け渡しに中央値で約7ミリ秒かかった（2026-11-02、85回）。
        # そこで、このコントローラの状態の受け口（DictProxy）を最初に1回だけ取り出し、以後は1回の往復で渡す。
        self.cstate = self.nx.manager_state[self.index]
        print("Switch とつながりました。", flush=True)

    @staticmethod
    def stick(v):
        # LLMCon の -32768..32767 を、nxbt の -100..100 にする
        return max(-100, min(100, round(v * 100 / 32767)))

    def hand(self, f):
        p = self.nx.create_input_packet()
        b = f["buttons"]
        for src, dst in self.face.items():
            p[dst] = bool(b & BITS[src])
        p["L"] = bool(b & BITS["LB"])
        p["R"] = bool(b & BITS["RB"])
        p["ZL"] = f["lt"] > 127
        p["ZR"] = f["rt"] > 127
        p["PLUS"] = bool(b & BITS["Start"])
        p["MINUS"] = bool(b & BITS["Back"])
        p["HOME"] = bool(b & BITS["Guide"])
        p["DPAD_UP"] = bool(b & BITS["DUp"])
        p["DPAD_DOWN"] = bool(b & BITS["DDown"])
        p["DPAD_LEFT"] = bool(b & BITS["DLeft"])
        p["DPAD_RIGHT"] = bool(b & BITS["DRight"])
        p["L_STICK"]["PRESSED"] = bool(b & BITS["LS"])
        p["R_STICK"]["PRESSED"] = bool(b & BITS["RS"])
        p["L_STICK"]["X_VALUE"] = self.stick(f["lx"])
        p["L_STICK"]["Y_VALUE"] = self.stick(f["ly"])
        p["R_STICK"]["X_VALUE"] = self.stick(f["rx"])
        p["R_STICK"]["Y_VALUE"] = self.stick(f["ry"])
        # nxbt は、渡された値が「何も押さず、スティックも 0」の値（DIRECT_INPUT_IDLE_PACKET）と
        # 文字列として同じだと、入力として読まず、スティックのバイトを 0（左下の端）のまま送る
        # （2026-11-02 に ROG Ally で確かめた）。値を float にすると比べた結果が違うので、毎回読ませられる。
        # nxbt はスティックの値を 100 で割って使うので、float でも同じ位置になる。
        p["L_STICK"]["X_VALUE"] = float(p["L_STICK"]["X_VALUE"])
        self.cstate["direct_input"] = p

    def close(self):
        try:
            self.nx.remove_controller(self.index)
        except Exception:
            pass


def main(argv=None):
    ap = argparse.ArgumentParser(description="LLMConHub の橋渡しのプログラム")
    ap.add_argument("--listen", default="127.0.0.1:9100", help="受け取る宛先とポート（既定 127.0.0.1:9100）")
    ap.add_argument("--backend", choices=["dump", "nxbt"], default="dump")
    ap.add_argument("--log", help="状態が変わった電文ごとの時刻を書く CSV のファイル")
    ap.add_argument("--quiet", action="store_true", help="dump で表示しない")
    ap.add_argument("--by-label", action="store_true", help="nxbt で、ボタンを位置ではなく名前で合わせる")
    ap.add_argument("--reconnect", help="nxbt で、前に接続した Switch へつなぎ直す。Bluetooth のアドレスか auto")
    ap.add_argument("--count", type=int, default=0, help="状態が変わった電文をこの数だけ受け取ったら終わる（試験のため）")
    args = ap.parse_args(argv)

    host, port = args.listen.rsplit(":", 1)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((host, int(port)))

    backend = NxbtBackend(args.by_label, args.reconnect) if args.backend == "nxbt" else DumpBackend(args.quiet)

    log = None
    writer = None
    if args.log:
        log = open(args.log, "w", newline="")
        writer = csv.writer(log)
        writer.writerow(["seq", "buttons", "input_us", "sent_us", "recv_us", "handed_us"])

    print(f"{args.listen} で待ち受けています（受け渡し先 {args.backend}）。", flush=True)
    last_seq = None
    lost = 0
    changes = 0
    try:
        while True:
            data, _ = sock.recvfrom(64)
            recv = now_us()
            f = decode(data)
            if f is None:
                continue
            if last_seq is not None and f["seq"] != (last_seq + 1) & 0xFFFFFFFF:
                lost += (f["seq"] - last_seq - 1) & 0xFFFFFFFF
            last_seq = f["seq"]
            backend.hand(f)
            handed = now_us()
            if f["heartbeat"]:
                continue
            changes += 1
            if writer:
                writer.writerow([f["seq"], f["buttons"], f["input_us"], f["sent_us"], recv, handed])
                log.flush()
            if args.count and changes >= args.count:
                break
    except KeyboardInterrupt:
        pass
    finally:
        backend.close()
        if log:
            log.close()
        print(f"取りこぼした電文の数: {lost}", file=sys.stderr)


if __name__ == "__main__":
    main()
