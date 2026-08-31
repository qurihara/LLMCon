#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""反応遅延がスキル差調整として効くことを、テトリスで測る。

# なぜこの作りにしたか

**対象は既存の著名なゲームである。** 自作のゲームでは「既存のゲームを外付けで拡張する」という
研究の主張が薄れる。ここでは権利の明確なテトリスの再実装（jakesgordon/javascript-tetris，MIT）を
そのまま使う。**ゲームのコードには一切手を加えていない。** LLMCon の出力は、ページに注入した
スクリプトがキーボードの事象へ変換して届ける。

**入力は人間入力の経路から送る。** LLMCon の改変ルールは人間入力にだけ効き，MCP のツールで
送る入力は「大規模言語モデルによる注入」として扱われて改変が効かない。

**自動操作は「見てから反応する」形にする。** 決まった手順を繰り返すだけでは，反応遅延を掛けても
同じことが遅れて起きるだけで成績に差が出ない。ここでは，いま落ちているブロックの列を読み，
目標の列との差を見て左右へ動かし，揃ったところで落とす。遅延があると，読んだ時点と操作が届く
時点がずれるので，意図した列に置けなくなる。

**1条件あたり10試行を下限とする。** 2回や3回では，ばらつきなのか効果なのか区別できない。

使い方:
    python measure_delay_tetris.py --trials 10
    python measure_delay_tetris.py --trials 20 --seconds 45
"""
import argparse
import json
import statistics
import sys
import time

sys.path.insert(0, '.')
from browser_gamepad import Browser
from llmcon import Mcp, VCon

GAME_URL = 'http://127.0.0.1:8082/index.html'
LLMCON = '127.0.0.1:8777'

# ボタンとキーの対応。テトリスは矢印キーとスペースで操作する
KEYS = {'DLeft': 'ArrowLeft', 'DRight': 'ArrowRight', 'DDown': 'ArrowDown',
        'A': 'ArrowUp', 'Start': 'Space'}

# 盤面を読む式。ゲームには手を加えていないので、素のグローバル変数を読む
READ = ("(()=>{ if(typeof playing==='undefined'||!playing) return null;"
        " return {x: current?current.x:null, y: current?current.y:null,"
        " score: score, rows: rows}; })()")


def play_once(page, v, seconds, target_cols=tuple(range(0, 10, 2)) + tuple(range(1, 10, 2))):
    """見てから反応する自動操作でテトリスを遊び、スコアと消したライン数を返す。

    目標の列を左から順に変えながら、いまのブロックをそこへ寄せて落とす。
    順に埋めていけば横一列が揃ってラインが消える。反応遅延があると、読んだ位置と
    操作が届く位置がずれて目標の列に置けなくなり、揃わなくなる。ライン数がそのまま
    遅延の効き方を表す。
    """
    page.reload(ignore_cache=True)
    time.sleep(1.8)

    v.release('DLeft'); v.release('DRight'); v.release('A'); v.release('Start')
    time.sleep(0.25)
    # スペースで開始する。反応遅延を掛けているときは、その分だけ届くのが遅れるので、
    # 決め打ちで待たずに playing になるまで待つ。ここを固定の待ち時間にしていたため、
    # 遅延の大きい条件でゲームが始まらず、1試行も測れないことがあった。
    for attempt in range(6):
        v.press('Start'); time.sleep(0.15); v.release('Start')
        started = False
        for _ in range(14):
            if page.eval("typeof playing!=='undefined' && playing === true") is True:
                started = True
                break
            time.sleep(0.12)
        if started:
            break
    time.sleep(0.3)

    t0 = time.time()
    ti = 0
    last_y = None
    last_x = None
    placed, hit = 0, 0          # 置いたブロックの数と、目標の列に置けた数
    while time.time() - t0 < seconds:
        st = page.eval(READ)
        if not st:
            break                      # 積み上がって終わった
        x, y = st.get('x'), st.get('y')
        if x is None:
            time.sleep(0.05); continue

        # 新しいブロックが出た（y が戻った）。直前のブロックがどこに置かれたかを数える
        if last_y is not None and y is not None and y < last_y:
            if last_x is not None:
                placed += 1
                if last_x == target_cols[ti]:
                    hit += 1
            ti = (ti + 1) % len(target_cols)
        last_y, last_x = y, x

        goal = target_cols[ti]
        if x < goal:
            v.press('DRight'); time.sleep(0.045); v.release('DRight')
        elif x > goal:
            v.press('DLeft'); time.sleep(0.045); v.release('DLeft')
        else:
            v.press('DDown'); time.sleep(0.045); v.release('DDown')
        time.sleep(0.02)

    v.release('DLeft'); v.release('DRight'); v.release('DDown')
    fin = page.eval("({score: (typeof score!=='undefined'?score:null),"
                    " rows: (typeof rows!=='undefined'?rows:null)})") or {}
    fin['placed'] = placed
    fin['hit'] = hit
    # 意図した列に置けた割合。反応遅延の効き方が最も素直に出る指標である
    fin['accuracy'] = (hit / placed * 100.0) if placed else None
    return fin


def summarize(name, xs):
    if not xs:
        return f"  {name}: 測れなかった"
    sd = statistics.pstdev(xs) if len(xs) > 1 else 0.0
    return (f"  {name}: 平均 {statistics.mean(xs):7.1f}  標準偏差 {sd:6.1f}  "
            f"中央値 {statistics.median(xs):6.1f}  最小 {min(xs):5}  最大 {max(xs):5}  "
            f"（{len(xs)}試行）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--trials', type=int, default=10)
    ap.add_argument('--seconds', type=float, default=30.0)
    ap.add_argument('--delays', type=str, default='0,50,100,150,200')
    args = ap.parse_args()
    delays = [int(x) for x in args.delays.split(',')]

    b = Browser()
    b.close_all()
    page = b.open(GAME_URL, llmcon=LLMCON, keys=KEYS, wait=False)
    time.sleep(2.0)
    v = VCon('127.0.0.1', 8777)
    m = Mcp('127.0.0.1', 8777, 'delay')
    time.sleep(0.5)

    print(f"テトリス（jakesgordon/javascript-tetris，コードは無改変）")
    print(f"1条件 {args.trials} 試行，1試行 {args.seconds} 秒")
    print()

    results = {}
    for d in delays:
        rules = [] if d == 0 else [{"op": "delay", "delayMs": d}]
        scores, rowsl = [], []
        print(f"  ── 反応遅延 {d} ミリ秒 ──", flush=True)
        for i in range(args.trials):
            m.call('set_mapping', rules=rules)
            r = play_once(page, v, args.seconds)
            if r.get('accuracy') is not None:
                scores.append(r['accuracy']); rowsl.append(r.get('placed') or 0)
            acc = r.get('accuracy')
            print(f"    {i+1:2d}/{args.trials}: 意図どおりに置けた割合 "
                  f"{acc if acc is None else round(acc,1)}％"
                  f"（{r.get('hit')}/{r.get('placed')}）", flush=True)
        m.call('set_mapping', rules=[])
        results[d] = {'score': scores, 'rows': rowsl}
        print()

    print("=" * 74)
    for d in delays:
        print(summarize(f"遅延{d:4d}ms 一致率(％)", results[d]['score']))
    print()
    base = results[delays[0]]['score']
    if base:
        bm = statistics.mean(base)
        for d in delays[1:]:
            xs = results[d]['score']
            if xs:
                print(f"  遅延{d:4d}ms: 改変なしに対して {(1 - statistics.mean(xs)/bm)*100:+.0f} パーセント")
    json.dump(results, open('measure_delay_tetris_result.json', 'w', encoding='utf-8'),
              ensure_ascii=False, indent=2)
    print("\n結果を measure_delay_tetris_result.json に書いた")
    v.close(); page.close()


if __name__ == '__main__':
    main()
