#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""反応遅延がスキル差調整として効くことを、テトリスで測る（ページ内で自動操作する版）。

前の版は、外から盤面を読んで操作を送っていた。読み取りのたびに通信の往復が生じ、
1秒に十数回も往復させるとページの処理が詰まって描画が止まる。反応遅延を掛けた条件ほど
ループが長引くので、**遅延の大きい条件だけ1試行も測れない**という形で表面化した。

この版は、盤面を読む・操作を決める・LLMCon へ人間入力として送る、の3つをページ内で回す。
外から呼ぶのは開始と結果の取得だけである。

対象は既存のテトリス（jakesgordon/javascript-tetris，MIT）で、**コードには一切手を加えていない**。
"""
import argparse, json, statistics, sys, time
sys.path.insert(0, '.')
from browser_gamepad import Browser, keyboard_js
from llmcon import Mcp

GAME_URL = 'http://127.0.0.1:8082/index.html'
LLMCON = '127.0.0.1:8777'
KEYS = {'DLeft': 'ArrowLeft', 'DRight': 'ArrowRight', 'DDown': 'ArrowDown',
        'A': 'ArrowUp', 'Start': 'Space'}
TARGETS = list(range(0, 10, 2)) + list(range(1, 10, 2))


def open_page(b, tick_ms):
    """キーボード変換と自動操作の両方を注入したページを開く。"""
    agent = (open('tetris_agent.js', encoding='utf-8').read()
             .replace('__HOST__', LLMCON)
             .replace('__TARGETS__', json.dumps(TARGETS))
             .replace('__TICK__', str(tick_ms)))
    import urllib.parse, urllib.request
    tab = json.loads(urllib.request.urlopen(urllib.request.Request(
        b.base + '/json/new?' + urllib.parse.quote(GAME_URL, safe=':/?=&.#'), method='PUT')).read())
    from browser_gamepad import Page
    page = Page(tab['webSocketDebuggerUrl'], tab['id'])
    page.call('Page.enable')
    page.call('Page.addScriptToEvaluateOnNewDocument',
              {'source': keyboard_js(KEYS, LLMCON)})
    page.call('Page.addScriptToEvaluateOnNewDocument', {'source': agent})
    page.reload(ignore_cache=True)
    return page


def play_once(page, seconds):
    page.reload(ignore_cache=True)
    time.sleep(2.2)
    page.eval("window.__tetrisAgent && window.__tetrisAgent.start()")
    time.sleep(seconds)
    page.eval("window.__tetrisAgent && window.__tetrisAgent.stop()")
    return page.eval("JSON.stringify(window.__tetrisAgent ? window.__tetrisAgent.result : null)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--trials', type=int, default=10)
    ap.add_argument('--seconds', type=float, default=25.0)
    ap.add_argument('--tick', type=int, default=70)
    ap.add_argument('--delays', type=str, default='0,50,100,150,200')
    args = ap.parse_args()
    delays = [int(x) for x in args.delays.split(',')]

    b = Browser(); b.close_all()
    page = open_page(b, args.tick)
    time.sleep(2.0)
    m = Mcp('127.0.0.1', 8777, 'delay2')

    print('テトリス（jakesgordon/javascript-tetris，コードは無改変）')
    print(f'1条件 {args.trials} 試行，1試行 {args.seconds} 秒，操作の間隔 {args.tick} ミリ秒')
    print()
    results = {}
    for d in delays:
        m.call('set_mapping', rules=([] if d == 0 else [{"op": "delay", "delayMs": d}]))
        acc = []
        print(f'  ── 反応遅延 {d} ミリ秒 ──', flush=True)
        for i in range(args.trials):
            raw = play_once(page, args.seconds)
            try:
                r = json.loads(raw) if isinstance(raw, str) else (raw or {})
            except Exception:
                r = {}
            a = r.get('accuracy')
            if a is not None:
                acc.append(a)
            print(f"    {i+1:2d}/{args.trials}: 一致率 "
                  f"{'-' if a is None else round(a,1)}％（{r.get('hit')}/{r.get('placed')}）",
                  flush=True)
        results[d] = acc
        print()
    m.call('set_mapping', rules=[])

    print('=' * 74)
    for d in delays:
        xs = results[d]
        if xs:
            sd = statistics.pstdev(xs) if len(xs) > 1 else 0.0
            print(f'  遅延{d:4d}ms: 平均 {statistics.mean(xs):5.1f}％  標準偏差 {sd:5.1f}  '
                  f'中央値 {statistics.median(xs):5.1f}  （{len(xs)}試行）')
        else:
            print(f'  遅延{d:4d}ms: 測れなかった')
    base = results[delays[0]]
    if base:
        bm = statistics.mean(base)
        print()
        for d in delays[1:]:
            if results[d]:
                print(f'  遅延{d:4d}ms: 改変なしに対して {(1 - statistics.mean(results[d])/bm)*100:+.0f} パーセント')
    json.dump(results, open('measure_delay_v2_result.json', 'w', encoding='utf-8'),
              ensure_ascii=False, indent=2)
    print('\n結果を measure_delay_v2_result.json に書いた')
    page.close()


if __name__ == '__main__':
    main()
