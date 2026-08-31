#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""6.2節の測定。スペースインベーダーで、感度を落とすと狙いが合わなくなることを測る。

対象は dwmkerr/spaceinvaders（MIT）である。キーボードだけを読むので、LLMCon の
出力をキーボードの事象へ変換する経路で届ける。**ゲームのコードには手を加えない。**

■ 何を指標にするか
  ゲームのスコアは使わない。スコアには、何体倒したかだけでなく、どれだけ生き延びたか
  や何回撃ったかが混ざるからである。代わりに「狙った位置に自機を置けた割合」を測る。
  狙う相手は、いちばん下の列の生きている侵略者である。自機と相手の x の差が、
  **このゲーム自身の命中判定と同じ幅**（侵略者の半幅、9ピクセル）のうちに収まって
  いる時間の割合を数える。撃てば当たる位置に自機を置けているか、を直接測っている。

■ 自動操作の作り
  誤差に比例した量だけスティックを倒す。K を比例定数とすると、倒す量は
  clamp(誤差 / K, -1, 1) である。キーボードへの変換はしきい値（0.4）で行うので、
  感度 g を掛けると、動き出すのに必要な誤差は 0.4 * K / g になる。すなわち
  **感度が下がると、細かい寄せができなくなる。** K は、感度1.0のときにこの値が
  許容幅の0.7倍になるように決める（素の感度では狙いが合う、という基準）。
  読む・決める・送るは、すべてページの中で回す。外との往復を増やすと測定が壊れる。

■ 条件
  改変あり  60秒かけて感度を 1.0 から 0.55 へ、10秒ごとの時間窓で段階的に落とす
  改変なし  60秒のあいだ感度を 1.0 のまま保つ（対照）
  どちらにも変化速度の上限を同じだけ掛ける。両条件で共通なので、差は感度だけに由来する。
  対照を置くのは、時間が経つとゲーム自体が難しくなる（侵略者が速くなる）ためである。
  これを見ないと、感度のせいなのかゲームのせいなのかが分からない。
  2つの条件を交互に走らせ、各10回ずつ繰り返す。

使い方: python3 measure_sensitivity.py [繰り返し回数]
"""
import json, re, sys, time

sys.path.insert(0, __file__.rsplit('/', 1)[0])
from browser_gamepad import Browser
from llmcon import Mcp

HOST = '127.0.0.1'
PORT = 8777
GAME = 'http://127.0.0.1:8083/index.html'
KEYS = {'DLeft': 'ArrowLeft', 'DRight': 'ArrowRight', 'A': 'Space'}

DURATION = 60.0          # 1回の長さ（秒）
STEP_SEC = 10.0          # 感度を落とす段の長さ
GAINS = [1.00, 0.91, 0.82, 0.73, 0.64, 0.55]
RATE = 8.0               # 変化速度の上限（毎秒。両条件で共通）

AGENT_JS = r"""
(() => {
  const WS = "ws://__HOST__/vcon/ws";
  let ws = null, ready = false;
  function connect() {
    try { ws = new WebSocket(WS); } catch (e) { setTimeout(connect, 300); return; }
    ws.onopen = () => { ready = true; };
    ws.onclose = () => { ready = false; setTimeout(connect, 300); };
  }
  connect();
  const send = (o) => { if (ready) try { ws.send(JSON.stringify(o)); } catch (e) {} };
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  const st = { running: false, t0: 0, samples: [], K: 16, tol: 9, firing: false, lastFire: 0 };

  function play() { return (game && game.currentState()
      && game.currentState().constructor.name === "PlayState") ? game.currentState() : null; }

  // 狙う相手。いちばん下の列の、いちばん左の生きている侵略者
  function target(s) {
    let best = null;
    for (const inv of s.invaders) {
      if (!best || inv.y > best.y + 1 || (Math.abs(inv.y - best.y) <= 1 && inv.x < best.x)) best = inv;
    }
    return best;
  }

  function tick() {
    if (!st.running) return;
    const s = play();
    if (!s || !s.ship || !s.invaders.length) {
      // 自機を失うと遊技の状態から抜ける。測定を続けるために自分で再開する。
      // 再開に費やした時間には標本が無いので、あとで段ごとの標本数を見ること。
      send({ t: "stick", s: "left", x: 0, y: 0 });
      const now = performance.now();
      if (now - st.lastFire > 600) {
        st.lastFire = now;
        send({ t: "btn", b: "A", d: true });
        setTimeout(() => send({ t: "btn", b: "A", d: false }), 60);
      }
      return;
    }
    const tg = target(s);
    if (!tg) return;
    const err = tg.x - s.ship.x;
    let x = err / st.K;
    if (x > 1) x = 1; if (x < -1) x = -1;
    send({ t: "stick", s: "left", x: x, y: 0 });
    const now = performance.now();
    if (now - st.lastFire > 260) {          // 撃ち続ける（遊びを進めるため）
      st.lastFire = now;
      send({ t: "btn", b: "A", d: true });
      setTimeout(() => send({ t: "btn", b: "A", d: false }), 45);
    }
    st.samples.push({
      ms: Math.round(now - st.t0),
      err: Math.round(err * 10) / 10,
      hit: Math.abs(err) <= st.tol ? 1 : 0,
      lv: s.level || null, lives: game.lives,
    });
  }
  setInterval(tick, 20);

  window.__siAgent = {
    get ready() { return ready; },
    get state() {
      const s = play();
      return { playing: !!s, invaders: s ? s.invaders.length : 0,
               score: game ? game.score : null, lives: game ? game.lives : null,
               level: game ? game.level : null };
    },
    // 遊べる状態にする。始まっていなければスペースにあたるボタンで進める
    async ensurePlaying(maxSec) {
      const t0 = performance.now();
      while (performance.now() - t0 < maxSec * 1000) {
        if (play() && play().invaders.length) return true;
        send({ t: "btn", b: "A", d: true }); await sleep(80);
        send({ t: "btn", b: "A", d: false }); await sleep(500);
      }
      return false;
    },
    setup(K, tol) { st.K = K; st.tol = tol; },
    begin() { st.samples = []; st.t0 = performance.now(); st.running = true; },
    end() {
      st.running = false;
      send({ t: "stick", s: "left", x: 0, y: 0 });
      send({ t: "btn", b: "A", d: false });
      return st.samples;
    },
  };
})();
"""


def evalp(page, expr, await_promise=True):
    r = page.eval(expr, await_promise=await_promise)
    if isinstance(r, dict) and '__error__' in r:
        raise RuntimeError(r['__error__'])
    return r


def ramp_rules():
    """時間窓を並べて、感度を段階的に落とすルールを作る。"""
    rules = []
    for k, g in enumerate(GAINS):
        rules.append({'op': 'gain', 'axis': 'LS', 'amount': g,
                      'startSec': k * STEP_SEC, 'endSec': (k + 1) * STEP_SEC})
    rules.append({'op': 'rate', 'axis': 'LS', 'amount': RATE})
    return rules


def flat_rules():
    return [{'op': 'gain', 'axis': 'LS', 'amount': GAINS[0]},
            {'op': 'rate', 'axis': 'LS', 'amount': RATE}]


def main():
    reps = int(sys.argv[1]) if len(sys.argv) > 1 else 10
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
    p.eval(AGENT_JS.replace('__HOST__', f'{HOST}:{PORT}'))
    for _ in range(25):
        if p.eval('!!(window.__siAgent && window.__siAgent.ready)') is True:
            break
        time.sleep(0.2)
    print('橋渡し:', p.eval('!!window.__llmconKeys'), '自動操作:', p.eval('window.__siAgent.ready'))

    print('遊べる状態にする:', evalp(p, 'window.__siAgent.ensurePlaying(20)'))
    info = evalp(p, '(() => { const s = game.currentState();'
                    ' return JSON.stringify({invW: s.invaders[0].width}); })()',
                 await_promise=False)
    tol = json.loads(info)['invW'] / 2.0        # このゲーム自身の命中判定と同じ幅
    K = 1.75 * tol                              # 感度1.0で、動き出しに要る誤差が許容幅の0.7倍
    p.eval(f'window.__siAgent.setup({K}, {tol})')
    print(f'許容幅 {tol} ピクセル（このゲームの命中判定と同じ）、比例定数 K = {K}')
    print(f'  動き出しに要る誤差: ' + '，'.join(
        f'感度{g}で{0.4 * K / g:.1f}px' for g in (GAINS[0], GAINS[-1])))

    runs = []
    for i in range(reps * 2):
        ramp = (i % 2 == 0)                     # 交互に走らせる
        label = '改変あり' if ramp else '改変なし'
        evalp(p, 'window.__siAgent.ensurePlaying(25)')
        m.call('set_mapping', rules=(ramp_rules() if ramp else flat_rules()))
        p.eval('window.__siAgent.begin()')
        t0 = time.time()
        while time.time() - t0 < DURATION:
            time.sleep(0.5)
        samples = p.eval('window.__siAgent.end()')
        m.call('set_mapping', rules=[])
        hit = sum(s['hit'] for s in samples)
        runs.append({'ramp': ramp, 'samples': samples})
        print(f'  {i+1:2d}/{reps*2} {label}  標本 {len(samples)}  '
              f'狙いが合っていた割合 {100*hit/max(len(samples),1):.1f}%')
        time.sleep(1.0)

    json.dump({'runs': runs, 'gains': GAINS, 'step_sec': STEP_SEC,
               'duration': DURATION, 'tol': tol, 'K': K, 'rate': RATE},
              open('measure_sensitivity_result.json', 'w'), ensure_ascii=False)
    print('\n書き出した: measure_sensitivity_result.json')

    # 段ごとの集計
    print('\n== 段ごとの「狙った位置に自機を置けた割合」 ==')
    print('  段  感度   改変あり        改変なし')
    for k, g in enumerate(GAINS):
        lo, hi = k * STEP_SEC * 1000, (k + 1) * STEP_SEC * 1000
        out = []
        for ramp in (True, False):
            per, nsum = [], 0
            for r in runs:
                if r['ramp'] != ramp:
                    continue
                ss = [s for s in r['samples'] if lo <= s['ms'] < hi]
                if ss:
                    per.append(100.0 * sum(s['hit'] for s in ss) / len(ss))
                    nsum += len(ss)
            if per:
                mean = sum(per) / len(per)
                sd = (sum((x - mean) ** 2 for x in per) / len(per)) ** 0.5
                out.append(f'{mean:5.1f}% (sd {sd:4.1f}, {len(per)}回, 標本{nsum}) ')
            else:
                out.append('   -')
        print(f'  {k+1}  {g:.2f}  {out[0]}  {out[1]}')


if __name__ == '__main__':
    main()
