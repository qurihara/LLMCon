#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""改変が難易度に効くことを、自動操作で測る。

# なぜこの作りにしたか

**入力は人間入力の経路から送る。** LLMCon の改変ルールは人間入力にだけ効き、MCP のツール
（hold や set_stick）で送る入力は「大規模言語モデルによる注入」として扱われるので改変が効かない。
これは設計どおりの性質だが、測定で踏むと「改変を掛けたのに差が出ない」という結果になる。
ここでは VCon（ウェブ版コントローラと同じ WebSocket の経路）から送る。

**自動操作は「見てから反応する」形にする。** 決まった手順を繰り返すだけでは、反応遅延を掛けても
同じことが遅れて起きるだけで成績に差が出ない。ゲームの状態を読み、それに応じて動く操作にして
初めて、遅延や感度の低下が成績に効く。ゲーム側には状態を読む口（window.__probe）を足してある。

**1条件あたり10試行を下限とする。** 2回や3回では、ばらつきなのか効果なのか区別できない。

使い方:
    python measure_difficulty.py                 # 既定（各条件10試行）
    python measure_difficulty.py --trials 20     # 20試行
"""
import argparse
import json
import statistics
import sys
import time

sys.path.insert(0, '.')
from browser_gamepad import Browser
from llmcon import Mcp, VCon

GAME_URL = 'http://127.0.0.1:8080/shooter.html?ws=127.0.0.1:8777'
LLMCON = '127.0.0.1:8777'


def play_once(page, v, seconds=14.0, tick=0.045):
    """自機を最寄りの敵へ寄せながら撃つ。状態を読んでから動くので、遅延が効く。"""
    page.reload(ignore_cache=True)
    time.sleep(1.6)
    page.wait_ready(5)

    v.release('A'); v.stick('left', 0, 0)
    time.sleep(0.2)
    v.press('A'); time.sleep(0.12); v.release('A')      # タイトルから開始
    time.sleep(0.7)

    t0 = time.time()
    firing = False
    while time.time() - t0 < seconds:
        st = page.eval("(()=>{const p=window.__probe; if(!p||!p.playing) return null;"
                       "const v=p.view; return {px:v.px, tx:v.target?v.target.x:null, alive:v.alive};})()")
        if not st:
            break
        tx = st.get('tx')
        if tx is None:
            v.stick('left', 0, 0)
        else:
            dx = tx - st['px']
            # 見えた位置に応じて寄せる。近ければ止めて撃つ
            if abs(dx) < 10:
                v.stick('left', 0, 0)
            else:
                v.stick('left', max(-1.0, min(1.0, dx / 90.0)), 0)
        if not firing:
            v.press('A'); firing = True
        time.sleep(tick)

    v.release('A'); v.stick('left', 0, 0)
    res = page.eval("JSON.stringify({score:__probe.score, state:__probe.state})")
    try:
        return json.loads(res)
    except Exception:
        return {'score': None, 'state': '?'}


def run(page, v, m, label, rules, trials, seconds):
    scores = []
    for i in range(trials):
        m.call('set_mapping', rules=rules)
        r = play_once(page, v, seconds=seconds)
        if r.get('score') is not None:
            scores.append(r['score'])
        print(f"    {label} {i+1:2d}/{trials}: スコア {r.get('score')}  状態 {r.get('state')}", flush=True)
    m.call('set_mapping', rules=[])
    return scores


def summarize(name, xs):
    if not xs:
        return f"  {name}: 測れなかった"
    mean = statistics.mean(xs)
    sd = statistics.pstdev(xs) if len(xs) > 1 else 0.0
    return (f"  {name}: 平均 {mean:.0f}  標準偏差 {sd:.0f}  "
            f"中央値 {statistics.median(xs):.0f}  最小 {min(xs)}  最大 {max(xs)}  （{len(xs)}試行）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--trials', type=int, default=10)
    ap.add_argument('--seconds', type=float, default=14.0)
    args = ap.parse_args()

    b = Browser()
    b.close_all()
    page = b.open(GAME_URL, llmcon=LLMCON)
    v = VCon(*LLMCON.split(':')[0:1], int(LLMCON.split(':')[1])) if False else VCon('127.0.0.1', 8777)
    m = Mcp('127.0.0.1', 8777, 'measure')
    time.sleep(0.5)

    print(f"1条件あたり {args.trials} 試行、1試行 {args.seconds} 秒で測る")
    print()
    conditions = [
        ('改変なし      ', []),
        ('感度0.45      ', [{"op": "gain", "axis": "LX", "amount": 0.45}]),
        ('反応遅延200ms ', [{"op": "delay", "delayMs": 200}]),
    ]
    results = {}
    for label, rules in conditions:
        print(f"  ── {label.strip()} ──", flush=True)
        results[label] = run(page, v, m, label.strip(), rules, args.trials, args.seconds)
        print()

    print("=" * 66)
    for label, xs in results.items():
        print(summarize(label.strip(), xs))
    base = results[conditions[0][0]]
    if base:
        bm = statistics.mean(base)
        for label, xs in list(results.items())[1:]:
            if xs:
                print(f"  {label.strip()}: 改変なしに対して {(1 - statistics.mean(xs)/bm)*100:+.0f} パーセント")
    json.dump({k.strip(): v for k, v in results.items()},
              open('measure_difficulty_result.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
    print("\n結果を measure_difficulty_result.json に書いた")
    v.close(); page.close()


if __name__ == '__main__':
    main()
