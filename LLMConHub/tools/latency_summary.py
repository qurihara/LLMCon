#!/usr/bin/env python3
"""橋渡しのプログラム（bridge/llmconhub_bridge.py --log）が書いた CSV から、遅延の指標を計算する。

計算する指標（どれも、状態が変わった電文ごとに求め、試行の全体で集計する）。
  A  LLMCon の中での処理の遅延 = sent_us − input_us
     物理のパッドの入力の事象（evdev のカーネルの時刻）から、LLMCon のホストが電文を送るまで。
     PC 版の約1.5ミリ秒と比べるための指標である。物理のパッドを読んでいないとき（input_us が 0）は求めない。
  A2 橋渡しのプログラムが受け取るまでの遅延 = recv_us − input_us
  H  橋渡しのプログラムが受け渡し先へ渡し終えるまでの遅延 = handed_us − input_us
  T  ホストから橋渡しへの送りの時間 = recv_us − sent_us

2つのプログラムが同じ時計を読んでいることが前提である。Linux ではどちらも CLOCK_MONOTONIC を読むはずだが、
mac では .NET の Stopwatch と Python の time.monotonic_ns の値が大きく違う（2026-10-30 に約74万秒の差を確かめた）。
T が負になるか1秒を超えるときは、時計が合っていないとみなして、A2 と H と T を出さない。
"""
import csv
import statistics
import sys


def summarize(name, xs):
    if not xs:
        print(f"{name}: 値がありません")
        return
    xs = sorted(xs)
    p95 = xs[min(len(xs) - 1, int(round(0.95 * (len(xs) - 1))))]
    print(f"{name}: 件数 {len(xs)}、中央値 {statistics.median(xs) / 1000:.3f} ミリ秒、平均 {statistics.mean(xs) / 1000:.3f}、"
          f"95パーセンタイル {p95 / 1000:.3f}、最小 {xs[0] / 1000:.3f}、最大 {xs[-1] / 1000:.3f}")


def main(path):
    rows = list(csv.DictReader(open(path)))
    a, a2, h, t = [], [], [], []
    clock_ok = True
    for r in rows:
        inp, sent, recv, handed = (int(r[k]) for k in ("input_us", "sent_us", "recv_us", "handed_us"))
        tt = recv - sent
        if tt < 0 or tt > 1_000_000:
            clock_ok = False
        t.append(tt)
        if inp:
            a.append(sent - inp)
            a2.append(recv - inp)
            h.append(handed - inp)
    summarize("A  LLMCon の中での処理の遅延", a)
    if clock_ok:
        summarize("A2 橋渡しが受け取るまで", a2)
        summarize("H  受け渡し先へ渡し終えるまで", h)
        summarize("T  ホストから橋渡しへの送り", t)
    else:
        print("注意: ホストと橋渡しの時計が合っていません（送りの時間が負か1秒を超える）。A2、H、T は出しません。")


if __name__ == "__main__":
    main(sys.argv[1])
