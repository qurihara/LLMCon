#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""6.5節の事例。画面を見て調整する審判のループを、実際に回す。

大規模言語モデルが数秒に一度だけゲーム画面を見て、成績の伸びを読み取り、
左スティックの感度を上下させる。**大規模言語モデルは、入力がゲームへ届く速い経路には
一切介在しない。** 行うのは改変ルールの差し替えだけである。

この道具は、大規模言語モデル自身が段階を追って呼ぶために作ってある。
1回の判断が1回の呼び出しに対応するので、判断と判断のあいだの時間が素直に測れる。

  python3 referee.py start           遊ばせはじめる。判断の記録を空にする
  python3 referee.py shot            画面を撮り、ファイル名と現在の様子を返す
  python3 referee.py decide G "理由"  感度を G にして、判断した時刻を記録する
  python3 referee.py report          判断の間隔と、そのあいだの高速実行層の速さを出す
  python3 referee.py stop            自動操作を止める

同じ枠組みで6.3節（劣勢の側の反応遅延量を審判が書き換える）も回せる。
そのときは decide の代わりに delay を使う。

  python3 referee.py delay MS "理由"  反応遅延を MS ミリ秒にして、判断を記録する
"""
import json, os, re, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from browser_gamepad import Browser, Page
from llmcon import Mcp

HOST, PORT = '127.0.0.1', 8777
GAME = 'http://127.0.0.1:8083/index.html'          # スペースインベーダーの再実装
KEYS = {'DLeft': 'ArrowLeft', 'DRight': 'ArrowRight', 'A': 'Space'}
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'referee_log.json')
SHOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'referee_shot.png')

# 自動操作は measure_sensitivity.py と同じものを使う（狙う相手を追いかけて撃つ）
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure_sensitivity import AGENT_JS


def page_of(br, frag):
    for t in br._http('/json/list'):
        if t.get('type') == 'page' and frag in t.get('url', ''):
            return Page(t['webSocketDebuggerUrl'], t['id'])
    return None


def load():
    return json.load(open(LOG)) if os.path.exists(LOG) else {'t0': None, 'decisions': []}


def save(d):
    json.dump(d, open(LOG, 'w'), ensure_ascii=False, indent=1)


def cmd_start():
    m = Mcp(HOST, PORT)
    for line in m.call('list_connections').splitlines():
        mm = re.match(r'\s*(c\d+):', line)
        if mm:
            m.call('remove_connection', id=mm.group(1))
    m.call('set_mapping', rules=[])

    br = Browser()
    br.close_all(); time.sleep(0.6)
    p = br.open(GAME, llmcon=f'{HOST}:{PORT}', keys=KEYS, wait=False)
    time.sleep(2.5); p.keep_awake()
    p.call('Emulation.setDeviceMetricsOverride',
           {'width': 820, 'height': 640, 'deviceScaleFactor': 2, 'mobile': False})
    p.eval(AGENT_JS.replace('__HOST__', f'{HOST}:{PORT}'))
    for _ in range(25):
        if p.eval('!!(window.__siAgent && window.__siAgent.ready)') is True:
            break
        time.sleep(0.2)
    p.eval('window.__siAgent.ensurePlaying(20)', await_promise=True)
    info = p.eval('(() => { const s = game.currentState();'
                  ' return JSON.stringify({invW: s.invaders[0].width}); })()')
    tol = json.loads(info)['invW'] / 2.0
    p.eval(f'window.__siAgent.setup({1.75 * tol}, {tol})')
    p.eval('window.__siAgent.begin()')
    save({'t0': time.time(), 'decisions': []})
    print('遊ばせはじめた。judge の記録を空にした。')
    print('いまの様子:', json.dumps(p.eval('window.__siAgent.state'), ensure_ascii=False))


def cmd_shot():
    br = Browser()
    p = page_of(br, '8083')
    if p is None:
        print('ゲームのページが見つからない。先に start を実行すること。'); return
    p.keep_awake()
    p.screenshot(SHOT)
    m = Mcp(HOST, PORT)
    st = m.call('get_state')
    hz = re.search(r'loop=(\d+)Hz', st)
    d = load()
    print(json.dumps({
        'shot': SHOT,
        'elapsed_sec': round(time.time() - d['t0'], 1) if d['t0'] else None,
        'decisions_so_far': len(d['decisions']),
        'loop_hz': int(hz.group(1)) if hz else None,
        'mapping': st,
    }, ensure_ascii=False))


def record(kind, value, reason):
    m = Mcp(HOST, PORT)
    if kind == 'gain':
        rules = [{'op': 'gain', 'axis': 'LS', 'amount': value}]
    else:
        rules = [{'op': 'delay', 'delayMs': value}]
    m.call('set_mapping', rules=rules)
    st = m.call('get_state')
    hz = re.search(r'loop=(\d+)Hz', st)
    d = load()
    now = time.time()
    prev = d['decisions'][-1]['t'] if d['decisions'] else None
    d['decisions'].append({'t': now, 'kind': kind, 'value': value,
                           'reason': reason, 'loop_hz': int(hz.group(1)) if hz else None})
    save(d)
    gap = f'{now - prev:.1f}秒' if prev else '（1回目）'
    print(f'{kind}={value} にした。前の判断からの間隔 {gap}。'
          f'高速実行層 {hz.group(1) if hz else "?"}Hz。理由: {reason}')


def cmd_report():
    d = load()
    ds = d['decisions']
    if len(ds) < 2:
        print('判断が2回に満たない。'); return
    gaps = [ds[i + 1]['t'] - ds[i]['t'] for i in range(len(ds) - 1)]
    mean = sum(gaps) / len(gaps)
    sd = (sum((g - mean) ** 2 for g in gaps) / len(gaps)) ** 0.5
    med = sorted(gaps)[len(gaps) // 2]
    hzs = [x['loop_hz'] for x in ds if x['loop_hz']]
    print(f'判断の回数 {len(ds)}，間隔の数 {len(gaps)}')
    print(f'判断の間隔  平均 {mean:.1f}秒  中央値 {med:.1f}秒  '
          f'標準偏差 {sd:.1f}秒  最小 {min(gaps):.1f}秒  最大 {max(gaps):.1f}秒')
    if hzs:
        print(f'そのあいだの高速実行層の速さ  平均 {sum(hzs)/len(hzs):.0f}Hz  '
              f'最小 {min(hzs)}Hz  最大 {max(hzs)}Hz')
        print(f'  1回のループの周期は約 {1000.0/(sum(hzs)/len(hzs)):.2f} ミリ秒である')
        print(f'  判断の周期は，ループの周期の約 {mean/(1.0/(sum(hzs)/len(hzs))):.0f} 倍')
        print(f'  毎秒60フレームの1フレーム（16.7ミリ秒）の約 {mean*1000/16.7:.0f} 倍')
    print(f'全体の長さ {ds[-1]["t"] - d["t0"]:.0f} 秒')
    print('\n判断の並び:')
    for i, x in enumerate(ds):
        g = f'{x["t"] - ds[i-1]["t"]:5.1f}秒' if i else '  --  '
        print(f'  {i+1:2d} {g}  {x["kind"]}={x["value"]}  {x["reason"]}')


def cmd_stop():
    br = Browser()
    p = page_of(br, '8083')
    if p:
        p.eval('window.__siAgent && window.__siAgent.end()')
    Mcp(HOST, PORT).call('set_mapping', rules=[])
    print('止めた。')


if __name__ == '__main__':
    a = sys.argv[1:] or ['report']
    if a[0] == 'start':
        cmd_start()
    elif a[0] == 'shot':
        cmd_shot()
    elif a[0] == 'decide':
        record('gain', float(a[1]), a[2] if len(a) > 2 else '')
    elif a[0] == 'delay':
        record('delay', float(a[1]), a[2] if len(a) > 2 else '')
    elif a[0] == 'report':
        cmd_report()
    elif a[0] == 'stop':
        cmd_stop()
    else:
        print(__doc__)
